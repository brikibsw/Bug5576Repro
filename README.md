# linq2db #5576 — spurious whole-object `[item]` column in an `AsQueryable()` LEFT JOIN with decimal arithmetic

Minimal, self-contained reproduction.

## Summary

LEFT JOINing a database query to an **in-memory collection materialized with `.AsQueryable()`** — where the
element is a **multi-member class** and one of its members flows into **decimal arithmetic** in a later
projection — makes the `VALUES` clause emit an extra, bogus column that binds the **whole element object**,
typed `Decimal`. At execution ADO.NET throws:

```
System.InvalidCastException : Failed to convert parameter value from a LeadsCount to a Decimal.
 ---> System.InvalidCastException : Object must implement IConvertible.
   at Microsoft.Data.SqlClient.SqlParameter.CoerceValue(...)
```

The query is built in three layers (mirroring a real repository: data query → data-model → domain-model):

1. **LEFT JOIN** the in-memory multi-member source (`leadCounts.AsQueryable()`),
2. a `.Select` that does **decimal arithmetic** on the joined member (`lead / (decimal)clicks * 100`),
3. a `.Select` that **re-maps** the result (data-model → domain-model).

Collapsing this to a single projection (rate computed inside the join selector, no re-map) produces a correct
2-column `VALUES` and does **not** reproduce.

## Affected versions

Reproduced on linq2db **6.2.1, 6.3.0**, and current **master (6.4.0, around commit `2e67bafc9`)**.
Provider: SQL Server (`Microsoft.Data.SqlClient`). This repro pins the released **6.3.0** NuGet package.

## How to run

Prerequisites: .NET 9 SDK and any reachable SQL Server (the repro only creates/drops one small `[Campaign]`
table). Provide a connection string via CLI arg or the `L2DB_CS` env var.

```bash
dotnet run -- "Server=localhost,1433;Database=tempdb;User Id=sa;Password=Your_Pwd;TrustServerCertificate=true;Encrypt=false"
```

## Actual (buggy) output

```
linq2db 6.3.0.0
BUG REPRODUCED:
  System.InvalidCastException: Failed to convert parameter value from a LeadsCount to a Decimal.
  ---> System.InvalidCastException: Object must implement IConvertible.

Generated SQL (note the spurious 3rd [item] column in the VALUES clause):
SELECT
	[r].[CampaignGuid],
	[lc].[Count],
	IIF([lc].[Count] IS NOT NULL, (CAST([lc].[Count] AS Decimal(18, 10)) / [r].[UniqueClicks]) * 100, NULL)
FROM
	[Campaign] [r]
		LEFT JOIN (VALUES
			('17f8bf6e-8e44-44e2-945c-f212e91e602e',5,@value)
		) [lc]([CampaignGuid], [Count], [item]) ON [r].[CampaignGuid] = [lc].[CampaignGuid]
```

`@value` is bound to the **whole `LeadsCount` instance** and declared `Decimal` → the cast fails at execution.
The first two columns (`CampaignGuid`, `Count`) are correct; the third `[item]` column is the bug.

## Expected output (and what a build with the fix produces)

```
linq2db 6.4.0.0
OK (1 row(s)) — bug NOT reproduced on this build.
  dfc8a18d-… leads=5 rate=50,000000000000000000000

Generated SQL:
...
		LEFT JOIN (VALUES
			('dfc8a18d-…',5)
		) [lc]([CampaignGuid], [Count]) ON [r].[CampaignGuid] = [lc].[CampaignGuid]
```

A correct **2-column** `VALUES` with no `[item]`.

## Root cause

`Source/LinqToDB/Internal/Linq/Builder/EnumerableContext.cs`, `MakeExpression`:

```csharp
var isScalar = MappingSchema.IsScalarType(ElementType);
if (isScalar || Builder.CurrentDescriptor != null)
{
    var dbType = Builder.CurrentDescriptor?.GetDbDataType(true) ?? MappingSchema.GetDbDataType(ElementType);
    if (isScalar || dbType.DataType != DataType.Undefined || ElementType.IsEnum)
    {
        ...
        var specialProp = SequenceHelper.CreateSpecialProperty(path, ElementType, "item");
        return specialProp;                       // renders the element as ONE "item" column
    }
}

var result = Builder.BuildFullEntityExpression(MappingSchema, path, ElementType, flags);   // decompose into columns
return result;
```

The "render as a single scalar `item` column" vs "decompose into columns" decision should depend on the
**element type** alone. But when `Builder.CurrentDescriptor != null` (a `Decimal` descriptor leaking in from
the surrounding projection arithmetic, `member / (decimal)…`), `dbType` is taken from the **descriptor**
(`Decimal`, `!= Undefined`) instead of the element type (`Undefined` for an unmapped multi-member class). So a
complex entity wrongly takes the scalar `[item]` path, and `CreateSpecialProperty` binds the whole element as
one parameter typed by that descriptor → `Decimal`. (`dbType` is used only for this decision; it is not passed
to `CreateSpecialProperty`.)

## Proposed fix

Base the decision on the element type only, never on `Builder.CurrentDescriptor`:

```diff
-    var isScalar = MappingSchema.IsScalarType(ElementType);
-    if (isScalar || Builder.CurrentDescriptor != null)
-    {
-        var dbType = Builder.CurrentDescriptor?.GetDbDataType(true) ?? MappingSchema.GetDbDataType(ElementType);
-        if (isScalar || dbType.DataType != DataType.Undefined || ElementType.IsEnum)
-        {
-            if (path.Type != ElementType)
-                path = ((ContextRefExpression)path).WithType(ElementType);
-            var specialProp = SequenceHelper.CreateSpecialProperty(path, ElementType, "item");
-            return specialProp;
-        }
-    }
+    var isScalar = MappingSchema.IsScalarType(ElementType);
+    var dbType   = MappingSchema.GetDbDataType(ElementType);
+    if (isScalar || dbType.DataType != DataType.Undefined || ElementType.IsEnum)
+    {
+        if (path.Type != ElementType)
+            path = ((ContextRefExpression)path).WithType(ElementType);
+        var specialProp = SequenceHelper.CreateSpecialProperty(path, ElementType, "item");
+        return specialProp;
+    }

     var result = Builder.BuildFullEntityExpression(MappingSchema, path, ElementType, flags);
     return result;
```

With this change the data query decomposes the multi-member element into its columns (correct 2-column
`VALUES`). Verified against the full linq2db test suite (9334 passing, 0 failing).

## Notes

- The element **must be a class**. An unmapped `struct` is treated as scalar by `IsScalarType` (a separate path).
- Removing the decimal arithmetic (so no `Decimal` descriptor is in scope) makes it build correctly —
  confirming the descriptor-leak diagnosis.
- The third re-mapping `.Select` matters: it keeps the decimal projection as an inner query so the element
  source is re-resolved while the `Decimal` descriptor is active.
