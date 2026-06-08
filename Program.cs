using System;
using System.Collections.Generic;
using System.Linq;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SqlServer;
using LinqToDB.Mapping;

namespace Bug5576Repro
{
    // ============================================================================================
    // linq2db #5576 — LEFT JOIN to an in-memory AsQueryable() of a MULTI-MEMBER class, where a
    // member flows into decimal arithmetic, emits a spurious whole-object "[item]" column in the
    // VALUES clause (typed Decimal). At execution ADO.NET throws:
    //     InvalidCastException: Failed to convert parameter value from a LeadsCount to a Decimal.
    //
    // The query is built in three layers, mirroring a real repository:
    //   (1) LEFT JOIN the in-memory multi-member source,
    //   (2) a .Select that does decimal arithmetic on the joined member (sets a Decimal descriptor),
    //   (3) a .Select that re-maps the result (data-model -> domain-model).
    // Collapsing this to a single projection (rate computed inside the join selector, no re-map)
    // builds a correct 2-column VALUES and does NOT reproduce.
    // ============================================================================================

    [Table("Campaign")]
    public sealed class Campaign
    {
        [Column, PrimaryKey] public int  CampaignID   { get; set; }
        [Column]             public Guid CampaignGuid { get; set; }
        [Column]             public int  UniqueClicks { get; set; }
    }

    // Multi-member class. MUST be a class (an unmapped struct is treated as scalar — different path).
    public sealed class LeadsCount
    {
        public Guid CampaignGuid { get; set; }
        public int  Count        { get; set; }
    }

    // Stage 1: result of the LEFT JOIN, carrying the (nullable) joined member.
    public sealed class Stat
    {
        public Guid CampaignGuid { get; set; }
        public int? LeadCount    { get; set; }
        public int  UniqueClicks { get; set; }
    }

    // Stage 2: adds the decimal rate computed from the joined member.
    public sealed class WithRate
    {
        public Guid     CampaignGuid { get; set; }
        public int?     LeadCount    { get; set; }
        public decimal? Rate         { get; set; }
    }

    // Stage 3: a re-mapping projection (e.g. data-model -> domain-model).
    public sealed class Result
    {
        public Guid     CampaignGuid { get; set; }
        public int?     Leads        { get; set; }
        public decimal? Rate         { get; set; }
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine($"linq2db {typeof(DataConnection).Assembly.GetName().Version}");

            var cs = args.Length > 0 ? args[0]
                : Environment.GetEnvironmentVariable("L2DB_CS")
                ?? "Server=localhost,14333;Database=l2db_b2;User Id=sa;Password=Replay!Dev2026x;TrustServerCertificate=true;Encrypt=false";

            using var db = new DataConnection(
                new DataOptions().UseSqlServer(cs, SqlServerVersion.v2022, SqlServerProvider.MicrosoftDataSqlClient));

            db.Execute("IF OBJECT_ID('Campaign') IS NOT NULL DROP TABLE [Campaign]");
            db.CreateTable<Campaign>();
            var guid = Guid.NewGuid();
            db.Insert(new Campaign { CampaignID = 1, CampaignGuid = guid, UniqueClicks = 10 });

            // In-memory lead counts, materialized via AsQueryable() and LEFT JOINed.
            var leadCounts = new Dictionary<Guid, int> { [guid] = 5 };
            var leads = leadCounts
                .Select(kv => new LeadsCount { CampaignGuid = kv.Key, Count = kv.Value })
                .AsQueryable();

            // Stage 1 — LEFT JOIN the in-memory multi-member source.
            var stage1 = db.GetTable<Campaign>()
                .LeftJoin(
                    leads,
                    (c, lc) => c.CampaignGuid == lc.CampaignGuid,
                    (c, lc) => new Stat
                    {
                        CampaignGuid = c.CampaignGuid,
                        LeadCount    = lc.Count,
                        UniqueClicks = c.UniqueClicks
                    });

            // Stage 2 — decimal arithmetic on the (nullable) joined member -> Decimal descriptor.
            var stage2 = stage1.Select(s => new WithRate
            {
                CampaignGuid = s.CampaignGuid,
                LeadCount    = s.LeadCount,
                Rate         = s.LeadCount.HasValue ? s.LeadCount.Value / (decimal)s.UniqueClicks * 100 : (decimal?)null
            });

            // Stage 3 — a re-mapping projection (data-model -> domain-model). THIS layering triggers the bug.
            var query = stage2.Select(r => new Result
            {
                CampaignGuid = r.CampaignGuid,
                Leads        = r.LeadCount,
                Rate         = r.Rate
            });

            try
            {
                var rows = query.ToList();
                Console.WriteLine($"OK ({rows.Count} row(s)) — bug NOT reproduced on this build.");
                foreach (var r in rows)
                    Console.WriteLine($"  {r.CampaignGuid} leads={r.Leads} rate={r.Rate}");
                Console.WriteLine();
                Console.WriteLine("Generated SQL:");
                Console.WriteLine(db.LastQuery);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("BUG REPRODUCED:");
                Console.WriteLine($"  {ex.GetType().FullName}: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"  ---> {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                Console.WriteLine();
                Console.WriteLine("Generated SQL (note the spurious 3rd [item] column in the VALUES clause):");
                Console.WriteLine(db.LastQuery);
                return 1;
            }
        }
    }
}
