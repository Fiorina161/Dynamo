# Third-party notices

Dynamo's own code is under the MIT licence (see `LICENSE`). The release zip also
contains the following third-party components, each under its own terms.

| Component | Used for | Licence |
|---|---|---|
| .NET runtime and ASP.NET Core (Microsoft) | The runtime the program runs on (bundled, so the target machine needs no .NET) | MIT |
| [Dapper](https://github.com/DapperLib/Dapper) | Database access to Sqlite | Apache-2.0 |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | Sqlite driver | MIT |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | Native Sqlite binding | Apache-2.0 |
| [SQLite](https://www.sqlite.org/copyright.html) | The database engine | Public domain |
| Microsoft.Extensions.Hosting.WindowsServices | Runs the site as a Windows service | MIT |
| System.Security.Cryptography.Pkcs | Cryptography (a patched dependency) | MIT |
| [htmx](https://htmx.org) (`wwwroot/lib`, bundled, no CDN) | Page updates without a build step | Zero-Clause BSD (`wwwroot/lib/htmx.LICENSE.txt`) |
| **Oracle.ManagedDataAccess.Core** (Oracle) | Connecting to Oracle databases | **Oracle Free Distribution, Hosting, and Use Terms and Conditions** (`licenses/Oracle.ManagedDataAccess.Core-LICENSE.txt`) |

## The Oracle driver

The Oracle driver is **not** covered by this project's MIT licence. Oracle
allows it to be used and redistributed unmodified, free of charge, on condition
that a copy of its licence travels with it, that Oracle's notices are kept, and
that you do not charge for its distribution. The release zip therefore includes
the licence text in `licenses/Oracle.ManagedDataAccess.Core-LICENSE.txt`: keep
it if you pass the zip on. If you use Dynamo with Oracle under an Oracle licence
agreement of your own, that agreement governs your use of the driver instead.
