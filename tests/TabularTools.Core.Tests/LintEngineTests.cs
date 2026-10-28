using TabularTools.Core.Config;
using TabularTools.Core.Lint;
using TabularTools.Core.Loading;

namespace TabularTools.Core.Tests;

public class LintEngineTests
{
    private static readonly Lazy<LoadedModel> Fixture = new(() => ModelLoader.Load(TestPaths.ExampleSalesPbip));
    private static readonly Lazy<LintResult> Default = new(() => LintEngine.Run(Fixture.Value, new LintOptions()));

    private static LintResult Lint(string modelPath, LintOptions? options = null) => LintEngine.Run(ModelLoader.Load(modelPath), options ?? new LintOptions());

    private static IEnumerable<string> Hits(LintResult result, string ruleId) => result.Violations.Where(v => v.RuleId == ruleId).Select(v => v.Object.Key);

    [Fact]
    public void Every_microsoft_rule_compiles_and_runs_without_errors()
    {
        var result = LintEngine.Run(Fixture.Value, new LintOptions { Rulesets = [RulesetLoader.MicrosoftBpa] });

        Assert.Equal(71, result.Rulesets.Single().Rules);
        Assert.Empty(result.SkippedRules);
        Assert.Empty(result.RuleErrors);
        Assert.Equal(71, result.Summary.RulesEvaluated);
    }

    [Theory]
    [InlineData("AVOID_FLOATING_POINT_DATA_TYPES", "table:Sales/column:Amount")]
    [InlineData("USE_THE_DIVIDE_FUNCTION_FOR_DIVISION", "table:Sales/measure:Margin")]
    [InlineData("AVOID_USING_THE_IFERROR_FUNCTION", "table:Sales/measure:Margin %")]
    [InlineData("DAX_COLUMNS_FULLY_QUALIFIED", "table:Sales/measure:Average Price")]
    [InlineData("NUMERIC_COLUMN_SUMMARIZE_BY", "table:Sales/column:CustomerKey")]
    [InlineData("HIDE_FOREIGN_KEYS", "table:Sales/column:CustomerKey")]
    [InlineData("UNNECESSARY_COLUMNS", "table:Sales/column:LoadBatchId")]
    [InlineData("PROVIDE_FORMAT_STRING_FOR_MEASURES", "table:Sales/measure:Margin")]
    [InlineData("REDUCE_USAGE_OF_CALCULATED_COLUMNS_THAT_USE_THE_RELATED_FUNCTION", "table:Sales/column:Product Category")]
    [InlineData("DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE", "table:Date")]
    [InlineData("MODEL_SHOULD_HAVE_A_DATE_TABLE", "model")]
    [InlineData("CHECK_IF_BI-DIRECTIONAL_AND_MANY-TO-MANY_RELATIONSHIPS_ARE_VALID", "relationship:Sales[CustomerKey]->Customer[CustomerKey]")]
    [InlineData("TT004", "table:Store/partition:Store")]
    public void Each_planted_anti_pattern_is_caught(string ruleId, string objectKey)
    {
        Assert.Contains(objectKey, Hits(Default.Value, ruleId));
    }

    [Fact]
    public void Clean_objects_are_not_flagged()
    {
        var result = Default.Value;

        Assert.DoesNotContain("table:Sales/partition:Sales", Hits(result, "TT004"));        // uses parameters
        Assert.DoesNotContain("table:Sales/column:Quantity", Hits(result, "UNNECESSARY_COLUMNS")); // referenced
        Assert.DoesNotContain("table:Sales/measure:Total Sales", Hits(result, "DAX_COLUMNS_FULLY_QUALIFIED"));
        Assert.Empty(Hits(result, "TT001"));
        Assert.Empty(Hits(result, "TT002"));
    }

    [Fact]
    public void Violations_are_ordered_and_rules_are_described_once()
    {
        var result = Default.Value;

        Assert.Equal(result.Violations.OrderByDescending(v => v.Severity).Select(v => v.Severity), result.Violations.Select(v => v.Severity));
        Assert.All(result.Violations, v => Assert.True(result.Rules.ContainsKey(v.RuleId)));
        Assert.Equal(result.Violations.Select(v => v.RuleId).Distinct().Count(), result.Rules.Count);
        Assert.Equal(Severity.Error, result.Rules["DAX_COLUMNS_FULLY_QUALIFIED"].Severity); // BPA severity 3
        Assert.Null(result.Rules["DAX_COLUMNS_FULLY_QUALIFIED"].Expression);
    }

    [Fact]
    public void Default_fail_on_is_error_and_blocking_follows_it()
    {
        Assert.Equal(FailOn.Error, Default.Value.FailOn);
        Assert.True(Default.Value.Blocking);

        var none = LintEngine.Run(Fixture.Value, new LintOptions { FailOn = FailOn.None });
        Assert.False(none.Blocking);

        var builtinOnly = LintEngine.Run(Fixture.Value, new LintOptions { Rulesets = [RulesetLoader.Builtin] });
        Assert.False(builtinOnly.Blocking); // only TT004 (info)
    }

    [Fact]
    public void Tt001_reports_broken_references_with_their_text()
    {
        using var model = new EditedModel().Replace("tables/Sales.tmdl", "measure 'Total Quantity' = SUM(Sales[Quantity])", "measure 'Total Quantity' = SUM(Sales[Qty]) + [Gone]");

        var result = Lint(model.Path, new LintOptions { Rulesets = [RulesetLoader.Builtin] });

        var v = Assert.Single(result.Violations, v => v.RuleId == "TT001");
        Assert.Equal("table:Sales/measure:Total Quantity", v.Object.Key);
        Assert.Equal(Severity.Error, v.Severity);
        Assert.Contains("Sales[Qty]", v.Message);
        Assert.Contains("[Gone]", v.Message);
    }

    [Fact]
    public void Tt002_flags_a_bidirectional_relationship_that_creates_a_second_path()
    {
        using var model = new EditedModel()
            .Replace("tables/Customer.tmdl", "	column Segment", "	column HomeStoreKey\n		dataType: int64\n		isHidden\n		summarizeBy: none\n		sourceColumn: HomeStoreKey\n\n	column Segment")
            .Append("relationships.tmdl", "relationship 00000000-0000-4000-8000-000000000299\n	fromColumn: Customer.HomeStoreKey\n	toColumn: Store.StoreKey\n\n");

        var result = Lint(model.Path, new LintOptions { Rulesets = [RulesetLoader.Builtin] });

        var v = Assert.Single(result.Violations, v => v.RuleId == "TT002");
        Assert.Equal("relationship:Sales[CustomerKey]->Customer[CustomerKey]", v.Object.Key);
        Assert.Contains("2 paths", v.Message);
    }

    [Fact]
    public void Tt003_flags_an_rls_filter_that_uses_another_tables_column_unqualified()
    {
        using var model = new EditedModel().Replace("roles/West Region.tmdl", "tablePermission Store = [Region] = \"West\"", "tablePermission Store = [Segment] = \"Retail\"");

        var result = Lint(model.Path, new LintOptions { Rulesets = [RulesetLoader.Builtin] });

        var v = Assert.Single(result.Violations, v => v.RuleId == "TT003");
        Assert.Equal("role:West Region/tablePermission:Store", v.Object.Key);
        Assert.Contains("'Customer'", v.Message);
    }

    [Fact]
    public void Tt005_flags_a_measure_named_like_a_column()
    {
        using var model = new EditedModel().Replace("tables/Product.tmdl", "measure 'Product Count'", "measure Category");

        var result = Lint(model.Path, new LintOptions { Rulesets = [RulesetLoader.Builtin] });

        var v = Assert.Single(result.Violations, v => v.RuleId == "TT005");
        Assert.Equal("table:Product/measure:Category", v.Object.Key);
        Assert.Contains("'Product'[Category]", v.Message);
    }

    [Fact]
    public void Config_overrides_severity_disables_rules_and_suppresses_with_globs()
    {
        var config = ConfigLoader.Parse("""
            lint:
              severity:
                AVOID_FLOATING_POINT_DATA_TYPES: error
                INTEGER_FORMATTING: off
              disabled: [OBJECTS_WITH_NO_DESCRIPTION]
              suppress:
                - rule: ISAVAILABLEINMDX_*
                  objects: "table:Sales/**"
                  reason: hidden fact columns are fine here
              failOn: warning
            """, "test");

        var result = LintEngine.Run(Fixture.Value, new LintOptions { Config = config });

        Assert.Equal(Severity.Error, result.Violations.Single(v => v.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES").Severity);
        Assert.Empty(Hits(result, "INTEGER_FORMATTING"));
        Assert.Empty(Hits(result, "OBJECTS_WITH_NO_DESCRIPTION"));
        Assert.DoesNotContain(Hits(result, "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS"), k => k.StartsWith("table:Sales/", StringComparison.Ordinal));
        Assert.Contains("table:Date/column:DateKey", Hits(result, "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS"));
        Assert.True(result.Summary.Suppressed > 0);
        Assert.Equal(FailOn.Warning, result.FailOn);
    }

    [Fact]
    public void Tabular_editor_ignore_annotations_are_honoured_on_objects_tables_and_the_model()
    {
        const string ignore = "annotation BestPracticeAnalyzer_IgnoreRules = {\"RuleIDs\":[\"AVOID_FLOATING_POINT_DATA_TYPES\",\"TT004\"]}";
        using var model = new EditedModel()
            .Replace("tables/Sales.tmdl", "		sourceColumn: Amount\n", "		sourceColumn: Amount\n\n		" + ignore + "\n")
            .Append("tables/Store.tmdl", "	" + ignore + "\n\n")
            .Replace("model.tmdl", "annotation PBI_QueryOrder", "annotation BestPracticeAnalyzer_IgnoreRules = {\"RuleIDs\":[\"MODEL_SHOULD_HAVE_A_DATE_TABLE\"]}\n\nannotation PBI_QueryOrder");

        var result = Lint(model.Path);

        Assert.Empty(Hits(result, "AVOID_FLOATING_POINT_DATA_TYPES"));
        Assert.Empty(Hits(result, "TT004"));                       // ignored on the partition's table
        Assert.Empty(Hits(result, "MODEL_SHOULD_HAVE_A_DATE_TABLE")); // ignored on the model
        Assert.True(result.Summary.Suppressed >= 3);
    }

    [Fact]
    public void Team_rule_files_load_with_extension_severity_and_duplicates_are_skipped()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("team.json"), """
            [
              {
                "ID": "TEAM_MEASURES_NEED_A_FOLDER",
                "Name": "Visible measures must have a display folder",
                "Category": "Maintenance",
                "Severity": 1,
                "Scope": "Measure",
                "Expression": "IsVisible and string.IsNullOrWhitespace(DisplayFolder)",
                "x-tabulartools": { "severity": "error", "helpUrl": "https://example.invalid/rules/folders" }
              },
              { "ID": "TT004", "Name": "Duplicate", "Scope": "Model", "Expression": "true" }
            ]
            """);

        var result = LintEngine.Run(Fixture.Value, new LintOptions { Rulesets = ["builtin", "team.json"], BaseDirectory = temp.Path });

        var v = Assert.Single(result.Violations, v => v.RuleId == "TEAM_MEASURES_NEED_A_FOLDER");
        Assert.Equal("table:Product/measure:Product Count", v.Object.Key);
        Assert.Equal(Severity.Error, v.Severity);
        Assert.Equal("https://example.invalid/rules/folders", result.Rules[v.RuleId].HelpUrl);
        Assert.Contains(result.SkippedRules, s => s.RuleId == "TT004" && s.Reason.Contains("Duplicate"));
    }

    [Fact]
    public void Unsupported_expressions_are_skipped_with_a_reason_and_runtime_errors_are_counted()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("odd.json"), """
            [
              { "ID": "UNKNOWN_MEMBER", "Name": "x", "Scope": "Measure", "Expression": "NoSuchProperty = 1" },
              { "ID": "UNKNOWN_SCOPE", "Name": "x", "Scope": "Variation", "Expression": "true" },
              { "ID": "THROWS", "Name": "x", "Scope": "Measure", "Expression": "Expression.Substring(500, 1) = \"x\"" },
              { "ID": "NEEDS_NEWER_MODEL", "Name": "x", "Scope": "Model", "Expression": "true", "CompatibilityLevel": 9999 }
            ]
            """);

        var result = LintEngine.Run(Fixture.Value, new LintOptions { Rulesets = ["odd.json"], BaseDirectory = temp.Path });

        Assert.Contains(result.SkippedRules, s => s.RuleId == "UNKNOWN_MEMBER" && s.Reason.Contains("NoSuchProperty"));
        Assert.Contains(result.SkippedRules, s => s.RuleId == "UNKNOWN_SCOPE" && s.Reason.Contains("Variation"));
        Assert.Contains(result.SkippedRules, s => s.RuleId == "NEEDS_NEWER_MODEL" && s.Reason.Contains("9999"));
        var error = Assert.Single(result.RuleErrors);
        Assert.Equal("THROWS", error.RuleId);
        Assert.Equal(11, error.Objects);
    }

    [Fact]
    public void Rule_and_severity_filters()
    {
        var onlyTwo = LintEngine.Run(Fixture.Value, new LintOptions { RuleIds = ["TT004", "AVOID_FLOATING_POINT_DATA_TYPES"] });
        Assert.Equal(["AVOID_FLOATING_POINT_DATA_TYPES", "TT004"], onlyTwo.Violations.Select(v => v.RuleId).Distinct().Order());

        var errorsOnly = LintEngine.Run(Fixture.Value, new LintOptions { MinSeverity = Severity.Error });
        Assert.All(errorsOnly.Violations, v => Assert.Equal(Severity.Error, v.Severity));
    }

    [Fact]
    public void Bad_ruleset_specs_are_reported()
    {
        Assert.Contains("aren't supported", Assert.Throws<ArgumentException>(() => RulesetLoader.Load("https://example.invalid/rules.json", ".")).Message);
        Assert.Contains("not found", Assert.Throws<ArgumentException>(() => RulesetLoader.Load("missing.json", TestPaths.Fixtures)).Message);

        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("bad.json"), "[ { not json");
        Assert.Contains("not valid BPA JSON", Assert.Throws<ArgumentException>(() => RulesetLoader.Load("bad.json", temp.Path)).Message);
    }
}
