using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dynamo.Modules.Settings;

/// <summary>
/// The https side: where it listens, and whether it exists at all (port 0 =
/// off, which is also the case when Dynamo listens on this computer only).
/// </summary>
public sealed record HttpsOptions(int Port)
{
    public bool Enabled => Port > 0;
}

/// <summary>
/// Dynamo's own certificates. A small certificate authority is created once
/// (valid ten years) and signs the server certificate (valid a little over a
/// year), which is renewed by itself before it expires or when the computer's
/// names or addresses change. The user trusts the authority once, in the
/// browser or in Windows; every renewal after that is trusted without a
/// warning. Both are kept, private key included, in the settings table, like
/// the other secrets of this database.
/// </summary>
public sealed class CertificateService
{
    public const int RenewDaysBefore = 30;
    public const int LeafValidityDays = 397;
    public const int AuthorityValidityYears = 10;

    private const string CaKey = "tls.ca";
    private const string LeafKey = "tls.leaf";
    private const string NamesKey = "tls.names";
    private const string RequireKey = "tls.require_remote";

    private readonly SettingsRepository _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<IReadOnlyList<string>> _names;
    private readonly object _gate = new();
    private X509Certificate2? _leaf;
    private X509Certificate2? _authority;
    private IReadOnlyList<string> _issuedFor = [];
    private bool? _requireRemote;

    /// <param name="now">The clock (tests move it).</param>
    /// <param name="names">The names and addresses the certificate is for (tests fix them).</param>
    public CertificateService(
        SettingsRepository settings, Func<DateTimeOffset>? now = null,
        Func<IReadOnlyList<string>>? names = null)
    {
        _settings = settings;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _names = names ?? DefaultNames;
    }

    /// <summary>The server certificate Kestrel presents (null until <see cref="Ensure"/> ran).</summary>
    public X509Certificate2? Current
    {
        get { lock (_gate) return _leaf; }
    }

    /// <summary>The authority's public certificate, DER-encoded, to be trusted (no private key).</summary>
    public byte[]? AuthorityCertificate
    {
        get { lock (_gate) return _authority?.RawData; }
    }

    /// <summary>SHA-256 of the authority's certificate, as "AB:CD:..." to compare by eye.</summary>
    public string? AuthorityFingerprint
    {
        get
        {
            var raw = AuthorityCertificate;
            return raw is null ? null : string.Join(':', SHA256.HashData(raw).Select(b => b.ToString("X2")));
        }
    }

    public DateTimeOffset? Expires
    {
        get { lock (_gate) return _leaf is null ? null : new DateTimeOffset(_leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero); }
    }

    /// <summary>The names and addresses the current certificate was issued for.</summary>
    public IReadOnlyList<string> IssuedFor
    {
        get { lock (_gate) return _issuedFor; }
    }

    /// <summary>
    /// Another computer using plain http is sent to https (default yes), once
    /// https exists. The certificate download itself is always left on http.
    /// </summary>
    public bool RequireHttpsForRemote
    {
        get
        {
            lock (_gate)
            {
                _requireRemote ??= _settings.Get(RequireKey) != "0";
                return _requireRemote.Value;
            }
        }
        set
        {
            _settings.Set(RequireKey, value ? "1" : "0");
            lock (_gate) _requireRemote = value;
        }
    }

    /// <summary>
    /// Makes sure there is a valid server certificate: creates the authority
    /// on first use, and issues a new certificate when there is none, when it
    /// expires within <see cref="RenewDaysBefore"/> days, when the names or
    /// addresses of this computer changed, or when asked to (force). Returns
    /// true when a new one was issued.
    /// </summary>
    public bool Ensure(bool force = false)
    {
        lock (_gate)
        {
            var authority = Load(CaKey) ?? CreateAuthority();
            var names = _names();
            var leaf = Load(LeafKey);
            var stored = (_settings.Get(NamesKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var current = !force
                && leaf is not null
                && leaf.NotAfter.ToUniversalTime() > _now().UtcDateTime.AddDays(RenewDaysBefore)
                && leaf.NotBefore.ToUniversalTime() <= _now().UtcDateTime
                && leaf.IssuerName.RawData.AsSpan().SequenceEqual(authority.SubjectName.RawData)
                && stored.OrderBy(n => n).SequenceEqual(names.OrderBy(n => n), StringComparer.OrdinalIgnoreCase);

            if (!current)
            {
                leaf?.Dispose();
                leaf = IssueServerCertificate(authority, names);
                _settings.Set(LeafKey, Convert.ToBase64String(leaf.Export(X509ContentType.Pfx)));
                _settings.Set(NamesKey, string.Join('\n', names));
            }

            _authority?.Dispose();
            _authority = authority;
            _leaf = leaf;
            _issuedFor = names;
            return !current;
        }
    }

    private X509Certificate2? Load(string key)
    {
        var text = _settings.Get(key);
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(text), null, X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;   // unreadable: it will be made again
        }
    }

    private X509Certificate2 CreateAuthority()
    {
        using var key = RSA.Create(3072);
        var name = new X500DistinguishedName($"CN=Dynamo local authority ({Environment.MachineName})");
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = _now();
        using var made = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(AuthorityValidityYears));
        var ca = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        _settings.Set(CaKey, Convert.ToBase64String(ca.Export(X509ContentType.Pfx)));
        // A new authority makes the old server certificate useless.
        _settings.Delete(LeafKey);
        return ca;
    }

    private X509Certificate2 IssueServerCertificate(X509Certificate2 authority, IReadOnlyList<string> names)
    {
        using var key = RSA.Create(2048);
        var subject = names.FirstOrDefault(n => !IPAddress.TryParse(n, out _)) ?? "localhost";
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={subject}"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));      // server authentication
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));

        var now = _now();
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;                                  // a positive number
        using var signed = request.Create(
            authority, now.AddDays(-1), now.AddDays(LeafValidityDays), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// What a browser may type to reach this computer: its name (short and
    /// full), localhost, and its IPv4 addresses.
    /// </summary>
    public static IReadOnlyList<string> DefaultNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost", "127.0.0.1", "::1", Environment.MachineName.ToLowerInvariant(),
        };
        try
        {
            var full = Dns.GetHostEntry(Dns.GetHostName()).HostName;
            if (!string.IsNullOrWhiteSpace(full))
                names.Add(full.ToLowerInvariant());
        }
        catch (SocketException)
        {
            // No DNS: the short name and the addresses will do.
        }
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                    if (address.AddressFamily == AddressFamily.InterNetwork
                        && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        names.Add(address.ToString());
        }
        catch (NetworkInformationException)
        {
            // Addresses unknown: the names will do.
        }
        return names.ToList();
    }
}

/// <summary>Looks again, now and then, whether the certificate needs renewing.</summary>
public sealed class CertificateRenewalService(
    CertificateService certificates, ILogger<CertificateRenewalService> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (certificates.Ensure())
                    log.LogInformation(
                        "A new https certificate was issued (expires {Expires:u}).", certificates.Expires);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                log.LogError(ex, "Renewing the https certificate failed; the current one stays in use.");
            }
        }
    }
}
