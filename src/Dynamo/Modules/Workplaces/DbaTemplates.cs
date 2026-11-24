namespace Dynamo.Modules.Workplaces;

/// <summary>What a panel is: an ordinary query, or the sessions panel (with its Kill button).</summary>
public static class PanelKinds
{
    public const string Query = "query";
    public const string Sessions = "sessions";
}

/// <summary>One ready-made panel a DBA can add to a workspace.</summary>
public sealed record DbaTemplate(
    string Key, string Group, string Title, string Description, string Sql,
    int RefreshSeconds = 0, string Kind = PanelKinds.Query);

/// <summary>
/// The DBA panels Dynamo knows, hard-coded: the operator picks some and they become ordinary panels
/// of a workspace (their query can be edited afterwards, only that panel changes). They are offered
/// on DBA connections only (the ones that log in AS SYSDBA), since they read the v$ and dba_ views.
/// To add one, add it to <see cref="All"/>: nothing else changes.
/// </summary>
public static class DbaTemplates
{
    /// <summary>The query of the sessions panel: active user sessions, never Dynamo's own.</summary>
    public const string SessionsSql = """
        select s.inst_id, s.sid, s.serial# as serial, s.username, s.status, s.osuser, s.machine,
               s.program, s.module, s.sql_id, s.event, s.wait_class,
               s.seconds_in_wait as wait_s, s.last_call_et as active_s,
               s.blocking_session as blocked_by,
               to_char(s.logon_time, 'YYYY-MM-DD HH24:MI:SS') as logon_time
          from gv$session s
         where s.type = 'USER'
           and s.status = 'ACTIVE'
           and not (s.inst_id = to_number(sys_context('USERENV', 'INSTANCE'))
                    and s.sid = to_number(sys_context('USERENV', 'SID')))
         order by s.last_call_et desc
        """;

    public static readonly IReadOnlyList<DbaTemplate> All =
    [
        new("sessions", "Sessions and locks", "Active sessions (with Kill)",
            "The active user sessions with who, where, what they wait for, and a red Kill button per row (asks for confirmation).",
            SessionsSql, 10, PanelKinds.Sessions),

        new("blocking", "Sessions and locks", "Blocking sessions",
            "Sessions waiting on a lock, with the session that blocks them.",
            """
            select w.inst_id, w.sid as waiting_sid, w.serial# as waiting_serial, w.username as waiting_user,
                   w.event, w.seconds_in_wait as wait_s, w.blocking_instance, w.blocking_session,
                   b.username as blocking_user, b.status as blocking_status, b.sql_id as blocking_sql_id
              from gv$session w
              left join gv$session b on b.inst_id = w.blocking_instance and b.sid = w.blocking_session
             where w.blocking_session is not null
             order by w.seconds_in_wait desc
            """, 10),

        new("locked-objects", "Sessions and locks", "Locked objects",
            "The objects currently locked, by which session and in which mode.",
            """
            select s.inst_id, s.sid, s.serial# as serial, s.username, o.owner, o.object_name, o.object_type,
                   decode(l.locked_mode, 0, 'None', 1, 'Null', 2, 'Row-S (SS)', 3, 'Row-X (SX)', 4, 'Share',
                          5, 'S/Row-X (SSX)', 6, 'Exclusive', to_char(l.locked_mode)) as locked_mode
              from gv$locked_object l
              join dba_objects o on o.object_id = l.object_id
              join gv$session s on s.inst_id = l.inst_id and s.sid = l.session_id
             order by o.owner, o.object_name
            """, 30),

        new("longops", "Sessions and locks", "Long operations",
            "Operations still running that Oracle tracks (full scans, backups, index builds...), with progress.",
            """
            select inst_id, sid, serial# as serial, username, opname, target, sofar, totalwork,
                   round(sofar / nullif(totalwork, 0) * 100, 1) as pct_done,
                   elapsed_seconds, time_remaining as seconds_left, sql_id
              from gv$session_longops
             where totalwork > 0 and sofar < totalwork
             order by time_remaining desc nulls last
            """, 15),

        new("tablespaces", "Space", "Tablespace usage",
            "Used and maximum size of every tablespace (autoextend taken into account), fullest first.",
            """
            select m.tablespace_name,
                   round(m.used_space * t.block_size / 1048576) as used_mb,
                   round(m.tablespace_size * t.block_size / 1048576) as max_mb,
                   round(m.used_percent, 1) as pct_used
              from dba_tablespace_usage_metrics m
              join dba_tablespaces t on t.tablespace_name = m.tablespace_name
             order by m.used_percent desc
            """, 60),

        new("temp", "Space", "Temporary space in use",
            "Who uses temporary tablespace right now, and how much.",
            """
            select s.inst_id, s.sid, s.serial# as serial, u.username, u.tablespace, u.segtype,
                   round(u.blocks * p.block_size / 1048576) as mb, u.sql_id
              from gv$tempseg_usage u
              join gv$session s on s.inst_id = u.inst_id and s.saddr = u.session_addr
             cross join (select to_number(value) as block_size from v$parameter where name = 'db_block_size') p
             order by u.blocks desc
            """, 30),

        new("undo", "Space", "Undo usage",
            "Undo extents by tablespace and state (active, unexpired, expired).",
            """
            select tablespace_name, status, round(sum(bytes) / 1048576) as mb, count(*) as extents
              from dba_undo_extents
             group by tablespace_name, status
             order by tablespace_name, status
            """, 60),

        new("fra", "Space", "Fast recovery area",
            "Limit, use and reclaimable space of the fast recovery area.",
            """
            select name, round(space_limit / 1048576) as limit_mb, round(space_used / 1048576) as used_mb,
                   round(space_reclaimable / 1048576) as reclaimable_mb,
                   round(space_used / nullif(space_limit, 0) * 100, 1) as pct_used, number_of_files
              from v$recovery_file_dest
            """, 120),

        new("segments", "Space", "Largest segments",
            "The 25 biggest segments. Heavy on a big database: it is not refreshed by itself.",
            """
            select * from (
                select owner, segment_name, segment_type, tablespace_name, round(bytes / 1048576) as mb
                  from dba_segments
                 order by bytes desc)
             where rownum <= 25
            """),

        new("top-sql", "Performance", "Top SQL by elapsed time",
            "The 25 statements that spent the most time since they entered the shared pool.",
            """
            select * from (
                select sql_id, plan_hash_value, executions, round(elapsed_time / 1e6, 1) as elapsed_s,
                       round(elapsed_time / nullif(executions, 0) / 1e6, 3) as per_exec_s,
                       round(cpu_time / 1e6, 1) as cpu_s, buffer_gets, disk_reads, rows_processed,
                       substr(sql_text, 1, 200) as sql_text
                  from v$sqlstats
                 order by elapsed_time desc)
             where rownum <= 25
            """, 60),

        new("waits", "Performance", "Wait events",
            "The 20 wait events with the most time waited since startup (idle waits left out).",
            """
            select * from (
                select event, wait_class, total_waits, round(time_waited_micro / 1e6) as time_s,
                       round(time_waited_micro / nullif(total_waits, 0) / 1000, 2) as avg_ms
                  from v$system_event
                 where wait_class <> 'Idle'
                 order by time_waited_micro desc)
             where rownum <= 20
            """, 60),

        new("sessions-by-user", "Performance", "Sessions by user",
            "How many sessions each user has, active and not.",
            """
            select username, count(*) as sessions,
                   sum(case when status = 'ACTIVE' then 1 else 0 end) as active
              from gv$session
             where type = 'USER'
             group by username
             order by sessions desc
            """, 30),

        new("backups", "Maintenance", "RMAN backups (7 days)",
            "The backup jobs of the last week with their status and size.",
            """
            select start_time, end_time, input_type, status, round(input_bytes / 1048576) as input_mb,
                   round(output_bytes / 1048576) as output_mb, elapsed_seconds
              from v$rman_backup_job_details
             where start_time > sysdate - 7
             order by start_time desc
            """, 300),

        new("archive-rate", "Maintenance", "Archived redo per hour",
            "How much redo was archived each hour of the last day.",
            """
            select trunc(first_time, 'HH24') as hour, count(*) as logs,
                   round(sum(blocks * block_size) / 1048576) as mb
              from v$archived_log
             where first_time > sysdate - 1 and dest_id = 1
             group by trunc(first_time, 'HH24')
             order by 1 desc
            """, 300),

        new("scheduler-failures", "Maintenance", "Failed scheduler jobs (24 h)",
            "Scheduler job runs of the last day that did not succeed.",
            """
            select log_date, owner, job_name, status, error# as error_no, run_duration, additional_info
              from dba_scheduler_job_run_details
             where status <> 'SUCCEEDED' and log_date > systimestamp - interval '1' day
             order by log_date desc
            """, 300),

        new("invalid-objects", "Maintenance", "Invalid objects",
            "Invalid objects counted by owner and type.",
            """
            select owner, object_type, count(*) as invalid
              from dba_objects
             where status = 'INVALID'
             group by owner, object_type
             order by invalid desc
            """, 300),

        new("alert-errors", "Maintenance", "Alert log errors (24 h)",
            "ORA- messages of the alert log in the last day (12c and later). Can be slow: not refreshed by itself.",
            """
            select originating_timestamp, message_text
              from v$diag_alert_ext
             where message_text like '%ORA-%' and originating_timestamp > systimestamp - interval '1' day
             order by originating_timestamp desc
            """),

        new("instance", "Database", "Instance and database",
            "Instance name, host, version, status, open mode, role and log mode.",
            """
            select i.inst_id, i.instance_name, i.host_name, i.version, i.status, i.startup_time,
                   d.name as db_name, d.open_mode, d.database_role, d.log_mode
              from gv$instance i
             cross join v$database d
            """, 120),

        new("dataguard-lag", "Database", "Data Guard lag",
            "Transport and apply lag, on a standby (empty on a primary).",
            "select name, value, unit, time_computed from v$dataguard_stats order by name", 60),
    ];

    public static DbaTemplate? Find(string? key) => All.FirstOrDefault(t => t.Key == key);

    /// <summary>The templates by group, in catalogue order (for the dialog).</summary>
    public static IEnumerable<IGrouping<string, DbaTemplate>> ByGroup() => All.GroupBy(t => t.Group);
}
