using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Cli;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
using AgentJoules.Core.Security;
using AgentJoules.Reporting;
using AgentJoules.Reporting.Endpoints;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentJoules.UnitTests;

[Trait("Spec", "REQ-004")]
public sealed class ConfigValidationTests
{
    private const double Tolerance = 1e-9;
    private const string EnvironmentName = "AGENTJOULES__Gpu__Index";

    private static string Key(string suffix) => AgentJoulesOptions.SectionName + ConfigurationPath.KeyDelimiter + suffix;

    // Keys are given relative to the AgentJoules section, as the story writes them (Meter:SampleRateHz).
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        ConfigurationFixtures.InMemory([.. pairs.Select(pair => (Key(pair.Key), pair.Value))]);

    private static AgentJoulesOptions Load(params (string Key, string Value)[] pairs) => OptionsLoader.Load(Config(pairs));

    private static ConfigurationException Rejected(params (string Key, string Value)[] pairs) =>
        Assert.Throws<ConfigurationException>(() => OptionsLoader.Load(Config(pairs)));

    private static void AssertDocumentedDefaults(AgentJoulesOptions options)
    {
        Assert.Equal(0, options.Gpu.Index);
        Assert.Equal("auto", options.Meter.Method);
        Assert.Equal(10.0, options.Meter.SampleRateHz, Tolerance);
        Assert.False(options.Meter.AllowBelowMinRate);
        Assert.Equal(30.0, options.Baseline.Seconds, Tolerance);
        Assert.Equal(0.10, options.Baseline.MaxCoefficientOfVariation, Tolerance);
    }

    // ---- criterion 1: the shipped file ----

    [Fact]
    public void TheShippedFileParsesAsJsonWithTheDocumentedValues()
    {
        string text = File.ReadAllText(ConfigurationFixtures.AppSettingsPath());

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement.GetProperty(AgentJoulesOptions.SectionName);

        Assert.Equal(0, root.GetProperty("Gpu").GetProperty("Index").GetInt32());
        Assert.Equal("auto", root.GetProperty("Meter").GetProperty("Method").GetString());
        Assert.Equal(10.0, root.GetProperty("Meter").GetProperty("SampleRateHz").GetDouble(), Tolerance);
        Assert.False(root.GetProperty("Meter").GetProperty("AllowBelowMinRate").GetBoolean());
        Assert.Equal(30.0, root.GetProperty("Baseline").GetProperty("Seconds").GetDouble(), Tolerance);
        Assert.Equal(0.10, root.GetProperty("Baseline").GetProperty("MaxCoefficientOfVariation").GetDouble(), Tolerance);
    }

    [Fact]
    public void TheShippedFileBindsToTheDocumentedDefaultsAndValidates()
    {
        AgentJoulesOptions options = OptionsLoader.Load(ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath()));

        AssertDocumentedDefaults(options);
    }

    [Fact]
    public void TheShippedFileAndAnEmptyConfigurationBindToTheSameValues()
    {
        AgentJoulesOptions fromFile = OptionsLoader.Bind(ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath()));
        AgentJoulesOptions fromNothing = OptionsLoader.Bind(ConfigurationFixtures.InMemory());

        Assert.Equal(fromNothing.Gpu.Index, fromFile.Gpu.Index);
        Assert.Equal(fromNothing.Meter.Method, fromFile.Meter.Method);
        Assert.Equal(fromNothing.Meter.SampleRateHz, fromFile.Meter.SampleRateHz, Tolerance);
        Assert.Equal(fromNothing.Meter.AllowBelowMinRate, fromFile.Meter.AllowBelowMinRate);
        Assert.Equal(fromNothing.Baseline.Seconds, fromFile.Baseline.Seconds, Tolerance);
        Assert.Equal(fromNothing.Baseline.MaxCoefficientOfVariation, fromFile.Baseline.MaxCoefficientOfVariation, Tolerance);
    }

    [Fact]
    public void TheMeterSectionsOfTheShippedFileHoldNoModelIdBaseUrlOrKey()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigurationFixtures.AppSettingsPath()));
        JsonElement root = document.RootElement.GetProperty(AgentJoulesOptions.SectionName);

        // The tier sections (AJ-043) hold endpoints, model IDs and key variable names; the sections of M1 still hold none of them.
        foreach (string section in new[] { "Gpu", "Meter", "Baseline" })
        {
            string text = root.GetProperty(section).GetRawText();

            Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Model", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Key", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- criterion 2: absent keys, mutable classes ----

    [Fact]
    public void AnEmptyConfigurationBindsToTheDocumentedDefaults()
    {
        AgentJoulesOptions options = OptionsLoader.Bind(ConfigurationFixtures.InMemory());

        AssertDocumentedDefaults(options);
    }

    [Fact]
    public void ANewOptionsObjectHoldsTheDocumentedDefaultsAndValidates()
    {
        var options = new AgentJoulesOptions();

        AssertDocumentedDefaults(options);
        OptionsLoader.ValidateOrThrow(options);
    }

    [Theory]
    [InlineData(typeof(AgentJoulesOptions))]
    [InlineData(typeof(GpuOptions))]
    [InlineData(typeof(MeterOptions))]
    [InlineData(typeof(BaselineOptions))]
    [InlineData(typeof(CostOptions))]
    public void TheOptionTypesAreSealedMutableClassesWithPlainGetAndSet(Type type)
    {
        Assert.True(type.IsClass);
        Assert.True(type.IsSealed);

        // A positional record is a class with a compiler-generated clone method; the binding generator does not bind it.
        Assert.Null(type.GetMethod("<Clone>$"));
        Assert.Null(type.GetCustomAttribute<RequiredMemberAttribute>());

        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);
        foreach (PropertyInfo property in properties)
        {
            Assert.NotNull(property.GetMethod);
            MethodInfo? setter = property.SetMethod;
            Assert.NotNull(setter);
            Assert.True(setter.IsPublic, property.Name + " needs a public setter");

            // An init accessor carries the IsExternalInit modifier on the return type of its setter; the generator never binds it.
            Assert.DoesNotContain(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
            Assert.Null(property.GetCustomAttribute<RequiredMemberAttribute>());
        }
    }

    [Fact]
    public void TheConfigurationFolderOfCoreHasNoRequiredInitOrRecord()
    {
        // The proof grep of criterion 2, `grep -rnE "required|init;|record " src/AgentJoules.Core/Configuration`, as a file scan.
        // The folder and its files must exist: a scan of nothing would pass.
        string folder = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "src", "AgentJoules.Core", "Configuration");
        string[] forbidden = ["required", "init;", "record "];

        Assert.True(Directory.Exists(folder), "src/AgentJoules.Core/Configuration does not exist");
        string[] files = [.. Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)];
        Assert.NotEmpty(files);

        List<string> offenders = [];
        foreach (string file in files)
        {
            offenders.AddRange(File.ReadLines(file)
                .Select((line, index) => (line, index))
                .Where(entry => forbidden.Any(word => entry.line.Contains(word, StringComparison.Ordinal)))
                .Select(entry => Path.GetRelativePath(folder, file) + ":" + (entry.index + 1).ToString(CultureInfo.InvariantCulture)));
        }

        Assert.Empty(offenders);
    }

    // ---- criterion 3: environment-style keys ----

    [Fact]
    public void EnvironmentOverrideBinds()
    {
        IConfiguration configuration = ConfigurationFixtures.InMemory((ConfigurationFixtures.NormaliseEnvironmentKey(EnvironmentName), "7"));

        AgentJoulesOptions options = OptionsLoader.Bind(configuration);

        Assert.Equal(7, options.Gpu.Index);

        // The key lived in the in-memory provider only; no test sets a process environment variable.
        Assert.Null(Environment.GetEnvironmentVariable(EnvironmentName));
    }

    [Fact]
    public void EnvironmentOverrideBindsWithALowercaseSection()
    {
        IConfiguration configuration = ConfigurationFixtures.InMemory((ConfigurationFixtures.NormaliseEnvironmentKey("agentjoules__gpu__index"), "7"));

        Assert.Equal(7, OptionsLoader.Bind(configuration).Gpu.Index);
    }

    [Fact]
    public void EnvironmentOverrideWinsOverTheShippedFileAndLeavesTheRestAlone()
    {
        IConfiguration configuration = ConfigurationFixtures.FromJsonFile(
            ConfigurationFixtures.AppSettingsPath(),
            (ConfigurationFixtures.NormaliseEnvironmentKey(EnvironmentName), "7"));

        AgentJoulesOptions options = OptionsLoader.Load(configuration);

        Assert.Equal(7, options.Gpu.Index);
        Assert.Equal("auto", options.Meter.Method);
        Assert.Equal(10.0, options.Meter.SampleRateHz, Tolerance);
    }

    [Theory]
    [InlineData("AGENTJOULES__Gpu__Index", "AGENTJOULES:Gpu:Index")]
    [InlineData("AGENTJOULES__Meter__AllowBelowMinRate", "AGENTJOULES:Meter:AllowBelowMinRate")]
    [InlineData("agentjoules__baseline__seconds", "agentjoules:baseline:seconds")]
    [InlineData("PLAIN", "PLAIN")]
    public void NormaliseEnvironmentKeyTurnsDoubleUnderscoresIntoColons(string name, string expected)
    {
        Assert.Equal(expected, ConfigurationFixtures.NormaliseEnvironmentKey(name));
    }

    // ---- criterion 4: the sample rate ----

    [Fact]
    public void FiveHertzWithoutTheOverrideFailsWithExitCodeTwoNamingBothKeys()
    {
        ConfigurationException failure = Rejected(("Meter:SampleRateHz", "5"), ("Meter:AllowBelowMinRate", "false"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal(2, (int)failure.ExitCode);
        Assert.Contains("Meter:SampleRateHz", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Meter:AllowBelowMinRate", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FiveHertzWithTheKeyAbsentFailsLikeFalse()
    {
        ConfigurationException failure = Rejected(("Meter:SampleRateHz", "5"));

        Assert.Contains("Meter:AllowBelowMinRate", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FiveHertzWithTheOverrideValidates()
    {
        AgentJoulesOptions options = Load(("Meter:SampleRateHz", "5"), ("Meter:AllowBelowMinRate", "true"));

        Assert.Equal(5.0, options.Meter.SampleRateHz, Tolerance);
        Assert.True(options.Meter.AllowBelowMinRate);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("20")]
    [InlineData("1000")]
    public void TenHertzAndAboveValidateWithoutTheOverride(string rate)
    {
        Assert.Equal(double.Parse(rate, CultureInfo.InvariantCulture), Load(("Meter:SampleRateHz", rate)).Meter.SampleRateHz, Tolerance);
    }

    [Fact]
    public void JustBelowTenHertzNeedsTheOverride()
    {
        ConfigurationException failure = Rejected(("Meter:SampleRateHz", "9.99"));

        Assert.Contains("Meter:AllowBelowMinRate", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", "true")]
    [InlineData("0", "false")]
    [InlineData("-1", "true")]
    [InlineData("-0.5", "true")]
    [InlineData("-5", "false")]
    public void AZeroOrNegativeRateFailsEvenWithTheOverride(string rate, string allowBelowMinRate)
    {
        ConfigurationException failure = Rejected(("Meter:SampleRateHz", rate), ("Meter:AllowBelowMinRate", allowBelowMinRate));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Meter:SampleRateHz", failure.Message, StringComparison.Ordinal);
    }

    // ---- criterion 5: the other ranges ----

    [Fact]
    public void AnUnknownMethodFailsNamingMeterMethod()
    {
        ConfigurationException failure = Rejected(("Meter:Method", "turbo"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Meter:Method", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("counter")]
    [InlineData("COUNTER")]
    [InlineData("sampling")]
    [InlineData("Sampling")]
    [InlineData(" auto ")]
    public void EveryMethodNameValidatesWithoutRegardToCase(string method)
    {
        Assert.Equal(method, Load(("Meter:Method", method)).Meter.Method);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-100")]
    public void ANegativeGpuIndexFailsNamingGpuIndex(string index)
    {
        ConfigurationException failure = Rejected(("Gpu:Index", index));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Gpu:Index", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    public void ANonNegativeGpuIndexValidates(string index)
    {
        Assert.Equal(int.Parse(index, CultureInfo.InvariantCulture), Load(("Gpu:Index", index)).Gpu.Index);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void AZeroOrNegativeBaselineWindowFailsNamingBaselineSeconds(string seconds)
    {
        ConfigurationException failure = Rejected(("Baseline:Seconds", seconds));

        Assert.Contains("Baseline:Seconds", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    public void AZeroOrNegativeCoefficientOfVariationLimitFailsNamingItsKey(string limit)
    {
        ConfigurationException failure = Rejected(("Baseline:MaxCoefficientOfVariation", limit));

        Assert.Contains("Baseline:MaxCoefficientOfVariation", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASmallPositiveBaselineWindowAndLimitValidate()
    {
        AgentJoulesOptions options = Load(("Baseline:Seconds", "0.5"), ("Baseline:MaxCoefficientOfVariation", "0.01"));

        Assert.Equal(0.5, options.Baseline.Seconds, Tolerance);
        Assert.Equal(0.01, options.Baseline.MaxCoefficientOfVariation, Tolerance);
    }

    [Fact]
    public void SeveralFailuresAreReportedTogetherInOneMessage()
    {
        ConfigurationException failure = Rejected(
            ("Gpu:Index", "-1"),
            ("Meter:Method", "turbo"),
            ("Meter:SampleRateHz", "5"),
            ("Baseline:Seconds", "0"),
            ("Baseline:MaxCoefficientOfVariation", "0"));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        foreach (string key in new[]
        {
            "Gpu:Index",
            "Meter:Method",
            "Meter:SampleRateHz",
            "Meter:AllowBelowMinRate",
            "Baseline:Seconds",
            "Baseline:MaxCoefficientOfVariation",
        })
        {
            Assert.Contains(key, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidateOrThrowRejectsAnObjectBuiltInCode()
    {
        var options = new AgentJoulesOptions();
        options.Meter.SampleRateHz = 5.0;

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Meter:SampleRateHz", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Meter:AllowBelowMinRate", failure.Message, StringComparison.Ordinal);
    }

    // ---- the bounds that match the real meter ----

    [Theory]
    [InlineData("1000")]
    [InlineData("999.9")]
    public void ARateUpToOneThousandHertzValidates(string rate)
    {
        Assert.Equal(double.Parse(rate, CultureInfo.InvariantCulture), Load(("Meter:SampleRateHz", rate)).Meter.SampleRateHz, Tolerance);
    }

    [Theory]
    [InlineData("1001")]
    [InlineData("1000.5")]
    [InlineData("1e300")]
    public void ARateAboveOneThousandHertzFailsNamingTheKey(string rate)
    {
        ConfigurationException failure = Rejected(("Meter:SampleRateHz", rate));

        Assert.Equal("Meter:SampleRateHz must be a finite number from 2.3283064376228985E-07 to 1000", failure.Message);
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("30")]
    [InlineData("4294967.294")]
    public void ABaselineWindowFromOneMillisecondToTheTimerLimitValidates(string seconds)
    {
        Assert.Equal(double.Parse(seconds, CultureInfo.InvariantCulture), Load(("Baseline:Seconds", seconds)).Baseline.Seconds, Tolerance);
    }

    [Theory]
    [InlineData("0.0005")]
    [InlineData("4294967.3")]
    [InlineData("1e300")]
    public void ABaselineWindowOutsideTheTimerLimitsFailsNamingTheKey(string seconds)
    {
        ConfigurationException failure = Rejected(("Baseline:Seconds", seconds));

        Assert.Equal("Baseline:Seconds must be a finite number from 0.001 to 4294967.294", failure.Message);
    }

    [Theory]
    [InlineData("Meter:SampleRateHz")]
    [InlineData("Baseline:Seconds")]
    [InlineData("Baseline:MaxCoefficientOfVariation")]
    public void NotANumberAndInfinityFailNamingTheKeyAndSayFinite(string key)
    {
        foreach (string value in new[] { "NaN", "Infinity", "-Infinity" })
        {
            ConfigurationException failure = Rejected((key, value));

            Assert.Contains(key + " must be a finite number", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AZeroRateIsReportedOnceWhenBothValidatorsRun()
    {
        var options = new AgentJoulesOptions();
        options.Meter.SampleRateHz = 0.0;

        var flat = new FlatSectionsValidator().Validate(null, options);
        var cross = new CrossFieldValidator().Validate(null, options);
        string[] lines = [.. (flat.Failures ?? []), .. (cross.Failures ?? [])];

        Assert.Single(lines);
        Assert.Contains("Meter:SampleRateHz", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCrossFieldValidatorLeavesNonPositiveAndNonFiniteRatesToTheAnnotation()
    {
        foreach (double rate in new[] { 0.0, -1.0, double.NaN, double.NegativeInfinity })
        {
            var options = new AgentJoulesOptions();
            options.Meter.SampleRateHz = rate;

            Assert.True(new CrossFieldValidator().Validate(null, options).Succeeded);
            Assert.False(new FlatSectionsValidator().Validate(null, options).Succeeded);
        }
    }

    // ---- the slowest rate the meter's timer accepts ----

    private const string LowestRate = "2.3283064376228985E-07";

    [Fact]
    public void TheLowestRateIsTheOneThatGivesTheLongestPeriodTheMeterAccepts()
    {
        // SampledEnergyMeter rejects a period of 1000 / rate milliseconds above uint.MaxValue - 1.
        Assert.Equal(LowestRate, MeterOptions.LowestRateHz.ToString("R", CultureInfo.InvariantCulture));
        Assert.True((1000.0 / MeterOptions.LowestRateHz) <= uint.MaxValue - 1.0);
    }

    [Fact]
    public void TheLowestRateValidatesWithTheOverride()
    {
        AgentJoulesOptions options = Load(("Meter:SampleRateHz", LowestRate), ("Meter:AllowBelowMinRate", "true"));

        Assert.Equal(MeterOptions.LowestRateHz, options.Meter.SampleRateHz);
    }

    [Fact]
    public void TheNextRateBelowTheLowestFailsEvenWithTheOverride()
    {
        string rate = double.BitDecrement(MeterOptions.LowestRateHz).ToString("R", CultureInfo.InvariantCulture);

        ConfigurationException failure = Rejected(("Meter:SampleRateHz", rate), ("Meter:AllowBelowMinRate", "true"));

        Assert.Contains("Meter:SampleRateHz", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1e-7")]
    [InlineData("1e-300")]
    public void ARateBelowTheLowestFailsEvenWithTheOverride(string rate)
    {
        ConfigurationException failure = Rejected(("Meter:SampleRateHz", rate), ("Meter:AllowBelowMinRate", "true"));

        Assert.Equal("Meter:SampleRateHz must be a finite number from 2.3283064376228985E-07 to 1000", failure.Message);
    }

    // ---- a value on the root key ----

    [Fact]
    public void AValueOnTheRootKeyInJsonIsRejected()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""{"AgentJoules":"x"}""")))
            .Build();

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.Load(configuration));

        Assert.Equal("AgentJoules is a section and must hold keys, not a value; the value is not shown", failure.Message);
    }

    [Fact]
    public void AValueOnTheRootKeyAsAnEnvironmentVariableIsRejected()
    {
        IConfiguration configuration = ConfigurationFixtures.InMemory((ConfigurationFixtures.NormaliseEnvironmentKey("AGENTJOULES"), "x"));

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.Load(configuration));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("AgentJoules is a section and must hold keys, not a value; the value is not shown", failure.Message);
    }

    [Fact]
    public void AValueOnTheRootKeyBesideKeysIsRejectedToo()
    {
        // A value beside keys is as much a mistake as a value alone, so it is reported; the keys beside it are still checked.
        IConfiguration configuration = ConfigurationFixtures.InMemory(("AgentJoules", "x"), ("AgentJoules:Gpu:Index", "3"));

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.Load(configuration));

        Assert.Equal("AgentJoules is a section and must hold keys, not a value; the value is not shown", failure.Message);
    }

    // ---- key segments in messages ----

    [Theory]
    [InlineData(42)]
    [InlineData(64)]
    [InlineData(33)]
    public void ALongUnknownKeySegmentIsNotEchoed(int length)
    {
        string segment = new('k', length);

        ConfigurationException failure = Rejected(("Gpu:" + segment, "3"));

        Assert.DoesNotContain(segment, failure.Message, StringComparison.Ordinal);
        Assert.Equal(
            "Gpu:a key segment of " + length.ToString(CultureInfo.InvariantCulture) + " characters is not a known option; check the spelling",
            failure.Message);
    }

    [Fact]
    public void AnUnknownKeySegmentOfThirtyTwoCharactersIsStillEchoed()
    {
        string segment = new('k', 32);

        ConfigurationException failure = Rejected(("Gpu:" + segment, "3"));

        Assert.Equal("Gpu:" + segment + " is not a known option; check the spelling", failure.Message);
    }

    [Theory]
    [InlineData(":x", "(empty key name) is not a known option; check the spelling")]
    [InlineData("Gpu::", "Gpu:(empty key name) is not a known option; check the spelling")]
    public void AnEmptyKeySegmentIsNamedNotPrintedAsNothing(string key, string expected)
    {
        ConfigurationException failure = Rejected((key, "1"));

        Assert.Equal(expected, failure.Message);
    }

    // ---- the typed keys ----

    [Fact]
    public void TheTypedKeyListCoversEveryNonStringScalarOption()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        // The dictionary section Tiers is checked per tier by TierConfigurationTests.The_typed_key_list_of_a_tier_covers_every_non_string_scalar_option;
        // it is skipped by name, and no other generic section may hide here.
        Assert.Equal(
            [nameof(AgentJoulesOptions.Tiers)],
            typeof(AgentJoulesOptions).GetProperties(flags).Where(section => section.PropertyType.IsGenericType).Select(section => section.Name));

        string[] expected =
        [
            .. typeof(AgentJoulesOptions).GetProperties(flags)
                .Where(section => section.PropertyType.IsClass && section.Name != nameof(AgentJoulesOptions.Tiers))
                .SelectMany(section => section.PropertyType.GetProperties(flags)
                    .Where(property => property.SetMethod is not null && property.PropertyType != typeof(string))
                    .Select(property => section.Name + ConfigurationPath.KeyDelimiter + property.Name)),
        ];

        // A new int, double or bool option that is not in the list would fall to the generic line when its value cannot be converted.
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Order(StringComparer.Ordinal), ConfigurationShape.TypedKeyNames.Order(StringComparer.Ordinal));
    }

    // ---- MinimumValueAttribute ----

    [Fact]
    public void MinimumValueAttributeAcceptsTheFiveNumericTypes()
    {
        var attribute = new MinimumValueAttribute(0.0) { IsExclusive = true };

        Assert.True(attribute.IsValid(1));
        Assert.True(attribute.IsValid(1L));
        Assert.True(attribute.IsValid(1f));
        Assert.True(attribute.IsValid(1.0));
        Assert.True(attribute.IsValid(1m));
        Assert.False(attribute.IsValid(0));
        Assert.False(attribute.IsValid(double.NaN));
        Assert.False(attribute.IsValid(double.PositiveInfinity));
    }

    [Fact]
    public void MinimumValueAttributeNeverParsesAStringAndNamesTheTypeNotTheValue()
    {
        var attribute = new MinimumValueAttribute(0.0);

        // The public IsValid(object) passes no context; it must not dereference one.
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => attribute.IsValid("12345-secret"));

        Assert.Contains("String", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MinimumValueAttributeIsOnlyAppliedToNumericProperties()
    {
        Type[] numeric = [typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal)];
        PropertyInfo[] annotated =
        [
            .. typeof(AgentJoulesOptions).Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(AgentJoulesOptions).Namespace)
                .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .Where(property => property.GetCustomAttribute<MinimumValueAttribute>() is not null),
        ];

        Assert.NotEmpty(annotated);
        // A nullable number (CloudGpu:HourlyPriceUsd, Cost:GCo2ePerKwh) is optional: unset passes, a set value is checked as the number it is.
        Assert.All(annotated, property => Assert.Contains(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, numeric));
    }

    // ---- a binding failure never shows a configured value ----

    private static readonly string KeyLikeValue = string.Concat("nvapi-", "abcdef1234567890");

    [Theory]
    [InlineData("Gpu:Index", "an integer")]
    [InlineData("Meter:SampleRateHz", "a number")]
    [InlineData("Meter:AllowBelowMinRate", "true or false")]
    [InlineData("Baseline:Seconds", "a number")]
    [InlineData("Baseline:MaxCoefficientOfVariation", "a number")]
    public void AValueOfTheWrongTypeNamesTheKeyAndNeverEchoesTheValue(string key, string expectedType)
    {
        ConfigurationException failure = Rejected((key, KeyLikeValue));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal(key + " must be " + expectedType + "; the value is not shown", failure.Message);
        Assert.DoesNotContain("abcdef1234567890", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef1234567890", failure.ToString(), StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("Gpu:Index")]
    [InlineData("Meter:SampleRateHz")]
    [InlineData("Meter:AllowBelowMinRate")]
    [InlineData("Baseline:Seconds")]
    [InlineData("Baseline:MaxCoefficientOfVariation")]
    public void AHostileValueGivesAOneLineMessageWithoutControlCharacters(string key)
    {
        ConfigurationException failure = Rejected((key, "\u001b[2J\r\nforged: line\u202E"));

        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", failure.Message, StringComparison.Ordinal);
        Assert.All(failure.Message, character => Assert.False(char.IsControl(character) || character == '\u202E'));
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("Gpu:Index")]
    [InlineData("Meter:SampleRateHz")]
    [InlineData("Meter:AllowBelowMinRate")]
    [InlineData("Baseline:Seconds")]
    [InlineData("Baseline:MaxCoefficientOfVariation")]
    public void AFiveThousandCharacterValueGivesAShortMessage(string key)
    {
        ConfigurationException failure = Rejected((key, new string('x', 5000)));

        Assert.True(failure.Message.Length < 200, "the message was " + failure.Message.Length.ToString(CultureInfo.InvariantCulture) + " characters");
    }

    // ---- a typo cannot silently keep a default ----

    [Theory]
    [InlineData("Gpu:Idx", "3")]
    [InlineData("Meter:Methd", "counter")]
    [InlineData("Baseline:Secs", "5")]
    [InlineData("Costs", "1")]
    public void AnUnknownKeyIsRejectedNamingIt(string key, string value)
    {
        ConfigurationException failure = Rejected((key, value));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal(key + " is not a known option; check the spelling", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void AnUnknownKeyIsMatchedWithoutRegardToCase()
    {
        Assert.Equal(3, Load(("gpu:INDEX", "3")).Gpu.Index);
    }

    [Fact]
    public void AHostileKeyNameCarriesNoControlCharactersIntoTheMessage()
    {
        ConfigurationException failure = Rejected(("Gpu:Id\u001b[2J\r\nforged\u202E", "3"));

        Assert.StartsWith("Gpu:Id", failure.Message, StringComparison.Ordinal);
        Assert.All(failure.Message, character => Assert.False(char.IsControl(character) || character == '\u202E'));
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData("Gpu:Index")]
    [InlineData("Meter:Method")]
    [InlineData("Meter:SampleRateHz")]
    [InlineData("Meter:AllowBelowMinRate")]
    [InlineData("Baseline:Seconds")]
    [InlineData("Baseline:MaxCoefficientOfVariation")]
    public void AKeyThatIsSetButEmptyIsRejectedNamingIt(string key)
    {
        ConfigurationException failure = Rejected((key, string.Empty));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal(key + " is set but empty", failure.Message);
    }

    [Fact]
    public void AValueGivenForAWholeSectionIsRejected()
    {
        ConfigurationException failure = Rejected(("Gpu", "3"));

        Assert.Equal("Gpu is a section and must hold keys, not a value; the value is not shown", failure.Message);
        Assert.DoesNotContain("3", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASectionGivenForASingleValueIsRejected()
    {
        ConfigurationException failure = Rejected(("Gpu:Index:Extra", "1"));

        Assert.Equal("Gpu:Index must be a single value, not a section", failure.Message);
    }

    [Fact]
    public void SeveralShapeProblemsAreReportedTogetherOnePerLine()
    {
        ConfigurationException failure = Rejected(("Gpu:Idx", "3"), ("Meter:Method", string.Empty));

        string[] lines = failure.Message.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.StartsWith("Gpu:Idx ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Meter:Method ", StringComparison.Ordinal));
    }

    // ---- criterion 7: one FlatSectionsValidator ----

    [Fact]
    public void ExactlyOneClassFlatSectionsValidatorExistsInSource()
    {
        // The proof grep of criterion 7, `grep -rn "class FlatSectionsValidator" src`, which prints 1 hit.
        string source = Path.Combine(ConfigurationFixtures.RepositoryRoot(), "src");

        int hits = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .SelectMany(file => File.ReadLines(file))
            .Count(line => line.Contains("class FlatSectionsValidator", StringComparison.Ordinal));

        Assert.Equal(1, hits);
    }

    [Theory]
    [InlineData("free-tier-no-price")]
    [InlineData("price-not-found")]
    [InlineData("contract-price-2026")]
    public void AMissingPriceReasonOfLowercaseWordsJoinedByHyphensValidates(string reason)
    {
        AgentJoulesOptions options = Load(("Tiers:nvidia:Enabled", "false"), ("Tiers:nvidia:MissingPriceReason", reason));

        Assert.Equal(reason, options.Tiers["nvidia"].MissingPriceReason);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("tab\there")]
    [InlineData("bell\a")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("Upper-case")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("under_score")]
    [InlineData("   ")]
    public void AMissingPriceReasonWithASpaceAControlCharacterOrAnotherShapeFailsNamingTheKeyWithoutTheValue(string reason)
    {
        ConfigurationException failure = Rejected(("Tiers:nvidia:Enabled", "false"), ("Tiers:nvidia:MissingPriceReason", reason));

        Assert.Contains("Tiers:nvidia:MissingPriceReason must be", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the value is not shown", failure.Message, StringComparison.Ordinal);
        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        if (reason.Trim().Length > 2)
        {
            Assert.DoesNotContain(reason, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AMissingPriceReasonOf64CharactersValidatesAndOf65FailsWithoutTheValue()
    {
        string longest = new('a', CrossFieldValidator.MaxReasonLength);
        string tooLong = new('b', CrossFieldValidator.MaxReasonLength + 1);

        Assert.Equal(longest, Load(("Tiers:nvidia:Enabled", "false"), ("Tiers:nvidia:MissingPriceReason", longest)).Tiers["nvidia"].MissingPriceReason);
        ConfigurationException failure = Rejected(("Tiers:nvidia:Enabled", "false"), ("Tiers:nvidia:MissingPriceReason", tooLong));

        Assert.Contains("Tiers:nvidia:MissingPriceReason must be", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tooLong, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedMissingPriceReasonValidates()
    {
        Assert.Equal("free-tier-no-price", ConfigurationFixtures.ShippedOptions().Tiers["nvidia"].MissingPriceReason);
    }

    private static bool IsBuildOutput(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase) || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    // ---- AJ-072: the Cost section and CloudGpu:HourlyPriceUsd ----

    [Theory]
    [InlineData("Cost:ElectricityPricePerKwh", "0")]
    [InlineData("Cost:ElectricityPricePerKwh", "-0.5")]
    [InlineData("Cost:ElectricityPricePerKwh", "NaN")]
    [InlineData("Cost:ElectricityPricePerKwh", "Infinity")]
    [InlineData("Cost:Currency", "")]
    [InlineData("Cost:Currency", "eur")]
    [InlineData("Cost:Currency", "EURO")]
    [InlineData("Cost:Currency", "E1R")]
    [InlineData("Cost:Currency", " EU")]
    [InlineData("Cost:GCo2ePerKwh", "0")]
    [InlineData("Cost:GCo2ePerKwh", "-5")]
    [InlineData("Cost:GCo2ePerKwh", "NaN")]
    [InlineData("CloudGpu:HourlyPriceUsd", "0")]
    [InlineData("CloudGpu:HourlyPriceUsd", "-1")]
    [InlineData("CloudGpu:HourlyPriceUsd", "Infinity")]
    public void AnInvalidCostOrRentalValueExitsTwoNamingTheKey(string key, string value)
    {
        ConfigurationException failure = Rejected((key, value));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Cost:ElectricityPricePerKwh", "-7.123")]
    [InlineData("Cost:ElectricityPricePerKwh", "not-a-number-zz")]
    [InlineData("Cost:Currency", "xq9")]
    [InlineData("Cost:GCo2ePerKwh", "-8.456")]
    [InlineData("CloudGpu:HourlyPriceUsd", "-9.789")]
    [InlineData("CloudGpu:HourlyPriceUsd", "not-a-number-zz")]
    public void ACostOrRentalFailureNamesTheKeyAndNeverTheValue(string key, string value)
    {
        ConfigurationException failure = Rejected((key, value));

        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACostSectionWithEveryKeySetAndARentalPriceValidate()
    {
        AgentJoulesOptions options = Load(
            ("Cost:ElectricityPricePerKwh", "0.25"),
            ("Cost:Currency", "SEK"),
            ("Cost:GCo2ePerKwh", "400"),
            ("CloudGpu:HourlyPriceUsd", "3.6"));

        Assert.Equal(0.25, options.Cost.ElectricityPricePerKwh, Tolerance);
        Assert.Equal("SEK", options.Cost.Currency);
        Assert.Equal(400.0, options.Cost.GCo2ePerKwh!.Value, Tolerance);
        Assert.Equal(3.6, options.CloudGpu.HourlyPriceUsd!.Value, Tolerance);
    }

    [Fact]
    public void TheCostDefaultsAreThirtyCentsInEuroWithNoFactorAndNoRentalPrice()
    {
        var options = new AgentJoulesOptions();

        Assert.Equal(0.30, options.Cost.ElectricityPricePerKwh, Tolerance);
        Assert.Equal("EUR", options.Cost.Currency);
        Assert.Null(options.Cost.GCo2ePerKwh);
        Assert.Null(options.CloudGpu.HourlyPriceUsd);
        OptionsLoader.ValidateOrThrow(options);
    }

    [Fact]
    public void TheShippedFileHoldsTheElectricityPriceAndCurrencyButNoFactorAndNoRentalPrice()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigurationFixtures.AppSettingsPath()));
        JsonElement root = document.RootElement.GetProperty(AgentJoulesOptions.SectionName);
        JsonElement cost = root.GetProperty("Cost");
        AgentJoulesOptions options = OptionsLoader.Load(ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath()));

        Assert.Equal(0.30, cost.GetProperty("ElectricityPricePerKwh").GetDouble(), Tolerance);
        Assert.Equal("EUR", cost.GetProperty("Currency").GetString());
        Assert.False(cost.TryGetProperty("GCo2ePerKwh", out _));
        Assert.False(root.GetProperty("CloudGpu").TryGetProperty("HourlyPriceUsd", out _));
        Assert.Null(options.Cost.GCo2ePerKwh);
        Assert.Null(options.CloudGpu.HourlyPriceUsd);
    }

    [Fact]
    public void AnUnknownCostKeyIsRejectedNamingIt()
    {
        ConfigurationException failure = Rejected(("Cost:Currncy", "EUR"));

        Assert.Contains("Cost:Currncy", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("eur")]
    public void AProgrammaticCurrencyThatIsNotACodeFailsNamingTheKeyOnly(string currency)
    {
        var options = new AgentJoulesOptions();
        options.Cost.Currency = currency;

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => OptionsLoader.ValidateOrThrow(options));

        Assert.Equal("Cost:Currency must be three upper-case letters", failure.Message);
    }

    [Fact]
    public void ACurrencyCodeIsExactlyThreeUpperCaseAsciiLetters()
    {
        string accented = string.Concat("E", ((char)0xC9).ToString(), "R");

        string[] codes = ["EUR", "USD", "SEK"];
        string?[] notCodes = [null, string.Empty, " ", "   ", "eur", "Eur", "EU", "EURO", "E1R", "E R", "EUR ", accented];

        Assert.All(codes, code => Assert.True(CostOptions.IsCurrencyCode(code), code));
        Assert.All(notCodes, code => Assert.False(CostOptions.IsCurrencyCode(code)));
    }

    // ---- AJ-086 (REQ-030): Report:RepositoryUrl ----


    private static string ReportUrl(string rest) => string.Concat(Uri.UriSchemeHttps, "://", "example.test/", rest);

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void TheShippedFileSetsTheRepositoryUrlOfTheProject()
    {
        AgentJoulesOptions options = ConfigurationFixtures.ShippedOptions();

        Assert.Equal(string.Concat(Uri.UriSchemeHttps, "://", "github.com/tomas-rampas/agent-joules"), options.Report.RepositoryUrl);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void ThereIsNoRepositoryUrlUnlessOneIsConfigured()
    {
        AgentJoulesOptions options = Load();

        Assert.Null(options.Report.RepositoryUrl);
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void AnAbsoluteHttpsRepositoryUrlIsAccepted()
    {
        string[] urls = [ReportUrl("owner/repo"), ReportUrl(string.Empty), string.Concat(Uri.UriSchemeHttps, "://", "example.test:8443/owner/repo")];

        Assert.All(urls, url => Assert.Equal(url, Load(("Report:RepositoryUrl", url)).Report.RepositoryUrl));
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void ARepositoryUrlThatIsNotAbsoluteHttpsWithoutUserInfoQueryOrFragmentIsRefusedWithoutShowingIt()
    {
        string secret = string.Concat("nvapi", "-", "Zq9_xK3", "_mP7w");
        string[] refused =
        [
            string.Concat(Uri.UriSchemeHttp, "://", "example.test/owner/repo"),
            string.Concat(Uri.UriSchemeFtp, "://", "example.test/owner/repo"),
            string.Concat(Uri.UriSchemeHttps, "://", "user:", secret, "@example.test/owner/repo"),
            string.Concat(Uri.UriSchemeHttps, "://", secret, "@example.test/owner/repo"),
            ReportUrl("owner/repo?a=1"),
            ReportUrl("owner/repo?"),
            ReportUrl("owner/repo#top"),
            ReportUrl("owner/repo#"),
            "owner/repo",
            "example.test/owner/repo",
            "   ",
            "/owner/repo",
        ];

        foreach (string url in refused)
        {
            ConfigurationException failure = Rejected(("Report:RepositoryUrl", url));

            Assert.Equal(ReportOptions.RepositoryUrlMessage, failure.Message);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void ARepositoryUrlWithAKeyInThePathOrSurroundingSpaceIsRefusedHere()
    {
        string key = string.Concat("nvapi", "-", "Zq9xK3mP7wAbCdEf");
        string[] refused =
        [
            ReportUrl("owner/" + key),
            ReportUrl(key + "/repo"),
            " " + ReportUrl("owner/repo"),
            ReportUrl("owner/repo") + " ",
            ReportUrl("owner/repo") + "\t",
        ];

        foreach (string url in refused)
        {
            ConfigurationException failure = Rejected(("Report:RepositoryUrl", url));

            Assert.Equal(ReportOptions.RepositoryUrlMessage, failure.Message);
            Assert.DoesNotContain(key, failure.Message, StringComparison.Ordinal);
            Assert.False(ReportOptions.IsRepositoryUrl(url, out Uri? uri));
            Assert.Null(uri);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-030")]
    public void TheReportSectionHoldsNoOtherKey()
    {
        ConfigurationException failure = Rejected(("Report:Other", "x"));

        Assert.Equal("Report:Other is not a known option; check the spelling", failure.Message);
    }

    // ---- AJ-089: Dashboard:PowerIntervalMs ----

    [Theory]
    [Trait("Spec", "REQ-032")]
    [InlineData("25")]
    [InlineData("49")]
    [InlineData("1001")]
    public void Power_interval_out_of_range_exits_2(string interval)
    {
        ConfigurationException failure = Rejected(("Dashboard:PowerIntervalMs", interval));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Dashboard:PowerIntervalMs must be a whole number from 50 to 1000", failure.Message);
        Assert.DoesNotContain(interval, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Spec", "REQ-032")]
    [InlineData("50")]
    [InlineData("100")]
    [InlineData("1000")]
    public void Power_interval_within_range_passes(string interval)
    {
        Assert.Equal(int.Parse(interval, CultureInfo.InvariantCulture), Load(("Dashboard:PowerIntervalMs", interval)).Dashboard.PowerIntervalMs);
    }

    [Theory]
    [Trait("Spec", "REQ-032")]
    [InlineData("100.5")]
    [InlineData("fast")]
    public void Power_interval_that_is_not_a_whole_number_exits_2_without_echoing_it(string interval)
    {
        ConfigurationException failure = Rejected(("Dashboard:PowerIntervalMs", interval));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Dashboard:PowerIntervalMs must be an integer", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(interval, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-032")]
    public void The_power_interval_defaults_to_100_and_the_shipped_file_says_so()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigurationFixtures.AppSettingsPath()));
        JsonElement root = document.RootElement.GetProperty(AgentJoulesOptions.SectionName);

        Assert.Equal(100, root.GetProperty("Dashboard").GetProperty("PowerIntervalMs").GetInt32());
        Assert.Equal("Dashboard", root.EnumerateObject().Last().Name);
        Assert.Equal(100, new AgentJoulesOptions().Dashboard.PowerIntervalMs);
        Assert.Equal(100, OptionsLoader.Bind(ConfigurationFixtures.InMemory()).Dashboard.PowerIntervalMs);
        Assert.Equal(100, OptionsLoader.Load(ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath())).Dashboard.PowerIntervalMs);
    }

    // ---- M3 exit: Judge:MaxOutputTokens ----

    [Theory]
    [Trait("Spec", "REQ-010")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("32769")]
    public void Judge_max_output_tokens_out_of_range_exits_2(string tokens)
    {
        ConfigurationException failure = Rejected(("Judge:MaxOutputTokens", tokens));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Judge:MaxOutputTokens must be a whole number from 1 to 32768", failure.Message);
        Assert.DoesNotContain(tokens, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Spec", "REQ-010")]
    [InlineData("1")]
    [InlineData("32")]
    [InlineData("32768")]
    public void Judge_max_output_tokens_within_range_passes(string tokens)
    {
        Assert.Equal(int.Parse(tokens, CultureInfo.InvariantCulture), Load(("Judge:MaxOutputTokens", tokens)).Judge.MaxOutputTokens);
    }

    [Theory]
    [Trait("Spec", "REQ-010")]
    [InlineData("32.5")]
    [InlineData("many")]
    public void Judge_max_output_tokens_that_is_not_a_whole_number_exits_2_without_echoing_it(string tokens)
    {
        ConfigurationException failure = Rejected(("Judge:MaxOutputTokens", tokens));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("Judge:MaxOutputTokens must be an integer", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tokens, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-010")]
    public void The_judge_reply_cap_defaults_to_32_and_the_shipped_file_says_so()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ConfigurationFixtures.AppSettingsPath()));
        JsonElement judge = document.RootElement.GetProperty(AgentJoulesOptions.SectionName).GetProperty("Judge");

        Assert.Equal(32, JudgeOptions.DefaultMaxOutputTokens);
        Assert.Equal(32, judge.GetProperty("MaxOutputTokens").GetInt32());
        Assert.Equal(32, new AgentJoulesOptions().Judge.MaxOutputTokens);
        Assert.Equal(32, OptionsLoader.Bind(ConfigurationFixtures.InMemory()).Judge.MaxOutputTokens);
        Assert.Equal(32, OptionsLoader.Load(ConfigurationFixtures.FromJsonFile(ConfigurationFixtures.AppSettingsPath())).Judge.MaxOutputTokens);
    }

    // ---- AJ-100: the endpoints document and the model-ID check of validate --config (REQ-018) ----

    private const string Verified = EndpointsFixtures.VerifiedDate;

    // A redactor that holds no key: the validator masks only what is registered.
    private static readonly SecretRedactor NoKeys = new();

    private static string Section(string tier, string? verified, params string[] modelIds) => EndpointsFixtures.Section(tier, verified, modelIds);

    private static EndpointsDocument Parsed(params string[] sections) => EndpointsDocParser.Parse(EndpointsFixtures.Document(sections));

    // The four tiers of the fixture options, each enabled with the model "model-<tier>", and the judge "model-nvidia" on the tier nvidia.
    private static AgentJoulesOptions EndpointOptions()
    {
        AgentJoulesOptions options = ProviderClientFixtures.FourTiers();
        options.Judge.Tier = "nvidia";
        options.Judge.ModelId = "model-nvidia";
        return options;
    }

    // A document that lists the model of every tier of EndpointOptions under a verified heading.
    private static EndpointsDocument CompleteDocument() =>
        Parsed(Section("local", Verified, "model-local"), Section("cloudgpu", Verified, "model-cloudgpu"), Section("nvidia", Verified, "model-nvidia"), Section("nebius", Verified, "model-nebius"));

    private static string[] OutputLines(string text) => [.. text.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0)];

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_parser_reads_the_verified_date_the_model_lines_and_the_status_of_each_tier_section()
    {
        string text = EndpointsFixtures.Document(
            Section("local", "2026-10-18", "nemotron-3-nano:4b"),
            "## cloudgpu\nStatus: pending (verified in M5, AJ-126)\n",
            Section("nvidia", Verified, "nvidia/a", "nvidia/b"),
            Section("nebius", null));

        EndpointsDocument document = EndpointsDocParser.Parse(text);

        Assert.Equal(["cloudgpu", "local", "nebius", "nvidia"], document.Sections.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new DateOnly(2026, 10, 18), document.Sections["local"].Verified);
        Assert.Equal(["nemotron-3-nano:4b"], document.Sections["local"].ModelIds);
        Assert.Null(document.Sections["cloudgpu"].Verified);
        Assert.Empty(document.Sections["cloudgpu"].ModelIds);
        Assert.Equal("pending (verified in M5, AJ-126)", document.Sections["cloudgpu"].Status);
        Assert.Equal(["nvidia/a", "nvidia/b"], document.Sections["nvidia"].ModelIds);
        Assert.Null(document.Sections["nvidia"].Status);
        Assert.Null(document.Sections["nebius"].Verified);
        Assert.Empty(document.Sections["nebius"].ModelIds);
        Assert.True(document.TryGetSection("nvidia", out EndpointsSection? nvidia));
        Assert.Equal("nvidia", nvidia.Name);
        Assert.False(EndpointsDocParser.Parse("# nothing here\n").TryGetSection("nvidia", out _));
    }

    [Theory]
    [InlineData("## Local")]
    [InlineData("## local (laptop)")]
    [InlineData("## notes")]
    [InlineData("##  local")]
    [InlineData("## nvidia-extra")]
    [Trait("Spec", "REQ-018")]
    public void Another_level_two_heading_ends_the_section_and_starts_an_ignored_one(string heading)
    {
        string text = "## nvidia\nVerified: " + Verified + "\n- model: kept\n" + heading + "\nVerified: 2030-01-01\n- model: ignored\nStatus: ignored\n";

        EndpointsSection nvidia = Assert.Single(EndpointsDocParser.Parse(text).Sections.Values);

        Assert.Equal(["kept"], nvidia.ModelIds);
        Assert.Equal(new DateOnly(2026, 10, 20), nvidia.Verified);
        Assert.Null(nvidia.Status);
    }

    [Theory]
    [InlineData("### local")]
    [InlineData("##local")]
    [InlineData(" ## local")]
    [InlineData("#### nebius")]
    [InlineData("# local")]
    [Trait("Spec", "REQ-018")]
    public void A_line_that_is_not_a_level_two_heading_is_prose_inside_the_current_section(string line)
    {
        string text = "## nvidia\n" + line + "\n- model: kept\n";

        EndpointsSection nvidia = Assert.Single(EndpointsDocParser.Parse(text).Sections.Values);

        Assert.Equal("nvidia", nvidia.Name);
        Assert.Equal(["kept"], nvidia.ModelIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_parser_ignores_the_text_before_the_first_heading_and_every_other_line()
    {
        string text = "Verified: 2030-01-01\n- model: before-any-heading\nStatus: before\n\n## local\nSome prose with - model: inside\n| table | row |\nverified: 2030-01-01\n* model: star\nVerified: " + Verified + "\n";

        EndpointsSection local = Assert.Single(EndpointsDocParser.Parse(text).Sections.Values);

        Assert.Equal(new DateOnly(2026, 10, 20), local.Verified);
        Assert.Empty(local.ModelIds);
        Assert.Null(local.Status);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void A_model_id_keeps_its_case_and_loses_only_its_trailing_white_space()
    {
        string text = "## nebius\n- model: Mixed-Case/ID:30B   \t\n- model: second\n- model:nospace\n-  model: double\n- model:  lead\n- model: \n- Model: upper\n* model: star\n";

        EndpointsSection nebius = Assert.Single(EndpointsDocParser.Parse(text).Sections.Values);

        Assert.Equal(["Mixed-Case/ID:30B", "second", " lead"], nebius.ModelIds);
        Assert.True(nebius.Lists("Mixed-Case/ID:30B"));
        Assert.False(nebius.Lists("mixed-case/id:30b"));
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void A_well_formed_verified_date_is_read_and_the_first_one_wins()
    {
        string text = "## local\nVerified: 2026-10-17\nVerified: 2026-11-01\n";

        EndpointsSection local = Assert.Single(EndpointsDocParser.Parse(text).Sections.Values);

        Assert.Equal(new DateOnly(2026, 10, 17), local.Verified);
        Assert.Null(local.MalformedVerified);
    }

    [Theory]
    [InlineData("2026-1-5")]
    [InlineData("2026/10/17")]
    [InlineData("17.10.2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("2026-10-17T00:00:00Z")]
    [InlineData("yesterday")]
    [Trait("Spec", "REQ-018")]
    public void A_malformed_verified_date_counts_as_missing_and_keeps_its_text(string value)
    {
        EndpointsSection local = Assert.Single(EndpointsDocParser.Parse("## local\nVerified: " + value + "\n").Sections.Values);

        Assert.Null(local.Verified);
        Assert.Equal(value, local.MalformedVerified);
    }

    [Theory]
    [InlineData("Verified:2026-10-17")]
    [InlineData("verified: 2026-10-17")]
    [InlineData(" Verified: 2026-10-17")]
    [InlineData("Verified: ")]
    [Trait("Spec", "REQ-018")]
    public void A_line_that_is_not_exactly_a_verified_line_is_ignored(string line)
    {
        EndpointsSection local = Assert.Single(EndpointsDocParser.Parse("## local\n" + line + "\n").Sections.Values);

        Assert.Null(local.Verified);
        Assert.Null(local.MalformedVerified);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void A_malformed_verified_date_before_a_good_one_does_not_hide_the_good_one()
    {
        EndpointsSection local = Assert.Single(EndpointsDocParser.Parse("## local\nVerified: soon\nVerified: 2026-10-17\n").Sections.Values);

        Assert.Equal(new DateOnly(2026, 10, 17), local.Verified);
        Assert.Null(local.MalformedVerified);
    }

    [Theory]
    [InlineData("nvidia")]
    [InlineData("local")]
    [Trait("Spec", "REQ-018")]
    public void A_duplicate_tier_heading_is_a_configuration_error_naming_the_heading(string tier)
    {
        string text = Section(tier, Verified, "a") + "## notes\nprose\n" + Section(tier, Verified, "b");

        ConfigurationException failure = Assert.Throws<ConfigurationException>(() => EndpointsDocParser.Parse(text));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Contains("## " + tier, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_parser_accepts_CRLF_line_ends()
    {
        string lf = EndpointsFixtures.Document(Section("local", Verified, "model-a  "), "## cloudgpu\nStatus: pending\n", Section("nvidia", Verified, "model-b"));
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        EndpointsDocument fromLf = EndpointsDocParser.Parse(lf);
        EndpointsDocument fromCrlf = EndpointsDocParser.Parse(crlf);

        Assert.Equal(fromLf.Sections.Keys.Order(StringComparer.Ordinal), fromCrlf.Sections.Keys.Order(StringComparer.Ordinal));
        foreach ((string name, EndpointsSection expected) in fromLf.Sections)
        {
            EndpointsSection actual = fromCrlf.Sections[name];
            Assert.Equal((expected.Verified, expected.Status, expected.MalformedVerified), (actual.Verified, actual.Status, actual.MalformedVerified));
            Assert.Equal(expected.ModelIds, actual.ModelIds);
        }

        Assert.Equal(["model-a"], fromCrlf.Sections["local"].ModelIds);
        Assert.Equal("pending", fromCrlf.Sections["cloudgpu"].Status);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_parser_rejects_null_text_and_reads_empty_text_as_no_sections()
    {
        Assert.Throws<ArgumentNullException>(() => EndpointsDocParser.Parse(null!));
        Assert.Empty(EndpointsDocParser.Parse(string.Empty).Sections);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void Every_configured_id_listed_in_its_section_validates_and_is_counted()
    {
        ConfigValidationResult result = ConfigModelValidator.Validate(EndpointOptions(), CompleteDocument(), NoKeys);

        Assert.Empty(result.Problems);
        Assert.Equal(5, result.ValidatedIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void An_id_absent_from_its_section_is_reported_with_the_section_name()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["nebius"].ModelId = "model-nebius-missing";

        ConfigValidationResult result = ConfigModelValidator.Validate(options, CompleteDocument(), NoKeys);

        Assert.Equal(["Missing in section ## nebius: model-nebius-missing"], result.Problems);
        Assert.Equal(5, result.ValidatedIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void An_id_listed_only_under_another_tier_is_missing_in_its_own_section()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["local"].ModelId = "model-nvidia";

        ConfigValidationResult result = ConfigModelValidator.Validate(options, CompleteDocument(), NoKeys);

        Assert.Equal(["Missing in section ## local: model-nvidia"], result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void Ids_are_compared_exactly_so_a_tag_that_differs_only_by_case_is_missing()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["local"].ModelId = "nemotron-3-nano:30B";
        EndpointsDocument document = Parsed(Section("local", Verified, "nemotron-3-nano:30b"), Section("cloudgpu", Verified, "model-cloudgpu"), Section("nvidia", Verified, "model-nvidia"), Section("nebius", Verified, "model-nebius"));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        Assert.Equal(["Missing in section ## local: nemotron-3-nano:30B"], result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_agent_ids_are_checked_too_and_each_distinct_id_of_a_tier_is_counted_once()
    {
        AgentJoulesOptions options = EndpointOptions();
        TierOptions nvidia = options.Tiers["nvidia"];
        nvidia.AgentModelIds["Planner"] = "model-nvidia";
        nvidia.AgentModelIds["Writer"] = "nvidia-writer";
        nvidia.AgentModelIds["Reviewer"] = "nvidia-writer";
        nvidia.AgentModelIds["Researcher"] = "nvidia-unlisted";
        EndpointsDocument document = Parsed(Section("local", Verified, "model-local"), Section("cloudgpu", Verified, "model-cloudgpu"), Section("nvidia", Verified, "model-nvidia", "nvidia-writer"), Section("nebius", Verified, "model-nebius"));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        Assert.Equal(["Missing in section ## nvidia: nvidia-unlisted"], result.Problems);
        Assert.Equal(7, result.ValidatedIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_judge_id_is_looked_up_in_the_section_of_the_judge_tier_and_counted_beside_the_tier_ids()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Judge.Tier = "nebius";
        options.Judge.ModelId = "judge-only-model";
        EndpointsDocument document = Parsed(Section("local", Verified, "model-local"), Section("cloudgpu", Verified, "model-cloudgpu"), Section("nvidia", Verified, "model-nvidia"), Section("nebius", Verified, "model-nebius", "judge-only-model"));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        Assert.Empty(result.Problems);
        Assert.Equal(5, result.ValidatedIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void A_judge_id_listed_only_under_another_tier_is_missing_in_the_judge_tier_section()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Judge.ModelId = "judge-only-model";
        EndpointsDocument document = Parsed(Section("local", Verified, "model-local"), Section("cloudgpu", Verified, "model-cloudgpu"), Section("nvidia", Verified, "model-nvidia"), Section("nebius", Verified, "model-nebius", "judge-only-model"));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        Assert.Equal(["Missing in section ## nvidia: judge-only-model"], result.Problems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "REQ-018")]
    public void A_judge_tier_that_is_disabled_or_has_no_section_reports_the_judge_id_as_missing(bool disabled)
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Judge.Tier = "nebius";
        options.Judge.ModelId = "model-nebius";
        EndpointsDocument document;
        if (disabled)
        {
            options.Tiers["nebius"].Enabled = false;
            document = CompleteDocument();
        }
        else
        {
            options.Tiers.Remove("nebius");
            document = Parsed(Section("local", Verified, "model-local"), Section("cloudgpu", Verified, "model-cloudgpu"), Section("nvidia", Verified, "model-nvidia"));
        }

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        Assert.Contains("Missing in section ## nebius: model-nebius", result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void Without_a_judge_id_nothing_is_checked_for_the_judge()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Judge.ModelId = null;

        ConfigValidationResult result = ConfigModelValidator.Validate(options, CompleteDocument(), NoKeys);

        Assert.Empty(result.Problems);
        Assert.Equal(4, result.ValidatedIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void A_disabled_tier_needs_only_its_heading_and_its_ids_are_not_checked_or_counted()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["cloudgpu"].Enabled = false;
        options.Tiers["cloudgpu"].ModelId = "not-listed-anywhere";
        EndpointsDocument document = Parsed(Section("local", Verified, "model-local"), "## cloudgpu\nStatus: pending (verified in M5, AJ-126)\n", Section("nvidia", Verified, "model-nvidia"), Section("nebius", Verified, "model-nebius"));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        Assert.Empty(result.Problems);
        Assert.Equal(4, result.ValidatedIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void A_disabled_tier_without_a_heading_is_reported()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["cloudgpu"].Enabled = false;
        EndpointsDocument document = Parsed(Section("local", Verified, "model-local"), Section("nvidia", Verified, "model-nvidia"), Section("nebius", Verified, "model-nebius"));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        Assert.Equal(["Section ## cloudgpu missing"], result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void An_enabled_tier_without_a_section_is_reported_and_its_ids_are_not_checked()
    {
        EndpointsDocument document = Parsed(Section("local", Verified, "model-local"), Section("cloudgpu", Verified, "model-cloudgpu"), Section("nebius", Verified, "model-nebius"));

        ConfigValidationResult result = ConfigModelValidator.Validate(EndpointOptions(), document, NoKeys);

        Assert.Equal(["Section ## nvidia missing", "Missing in section ## nvidia: model-nvidia"], result.Problems);
        Assert.Equal(4, result.ValidatedIds);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("soon", "soon")]
    [InlineData("2026-1-5", "2026-1-5")]
    [Trait("Spec", "REQ-018")]
    public void An_enabled_tier_without_a_valid_verified_date_is_reported_before_its_ids(string? verified, string? malformed)
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["nebius"].ModelId = "model-nebius-missing";
        EndpointsDocument document = Parsed(Section("local", Verified, "model-local"), Section("cloudgpu", Verified, "model-cloudgpu"), Section("nvidia", Verified, "model-nvidia"), Section("nebius", verified, "model-nebius"));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        string[] expected = malformed is null
            ? ["Verified date missing in section ## nebius", "Missing in section ## nebius: model-nebius-missing"]
            : ["Verified date missing in section ## nebius", $"Verified date malformed in section ## nebius: {malformed}", "Missing in section ## nebius: model-nebius-missing"];
        Assert.Equal(expected, result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void A_tier_the_configuration_does_not_hold_needs_no_section()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers.Remove("cloudgpu");
        EndpointsDocument document = Parsed(Section("local", Verified, "model-local"), Section("nvidia", Verified, "model-nvidia"), Section("nebius", Verified, "model-nebius"));

        ConfigValidationResult result = ConfigModelValidator.Validate(options, document, NoKeys);

        Assert.Empty(result.Problems);
        Assert.Equal(4, result.ValidatedIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_problems_follow_the_tier_order_of_a_run_then_the_judge_and_a_repeated_line_appears_once()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["nebius"].ModelId = "n";
        options.Tiers["local"].ModelId = "l";
        options.Tiers["nvidia"].ModelId = "v";
        options.Judge.ModelId = "v";

        ConfigValidationResult result = ConfigModelValidator.Validate(options, CompleteDocument(), NoKeys);

        Assert.Equal(["Missing in section ## local: l", "Missing in section ## nvidia: v", "Missing in section ## nebius: n"], result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_shipped_tier_models_and_judge_validate_against_a_document_built_from_them()
    {
        AgentJoulesOptions shipped = ConfigurationFixtures.ShippedOptions();
        var sections = new List<string>();
        foreach (string tier in TierOptions.KnownNames)
        {
            TierOptions entry = shipped.Tiers[tier];
            string[] ids = [.. new[] { entry.ModelId }.Concat(entry.AgentModelIds.Values).Append(tier == shipped.Judge.Tier ? shipped.Judge.ModelId : null).OfType<string>().Distinct(StringComparer.Ordinal)];
            sections.Add(Section(tier, Verified, ids));
        }

        ConfigValidationResult result = ConfigModelValidator.Validate(shipped, Parsed([.. sections]), NoKeys);

        Assert.Empty(result.Problems);
        Assert.Equal(shipped.Tiers.Values.Count(static tier => tier.Enabled) + 1, result.ValidatedIds);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_validator_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigModelValidator.Validate(null!, CompleteDocument(), NoKeys));
        Assert.Throws<ArgumentNullException>(() => ConfigModelValidator.Validate(EndpointOptions(), null!, NoKeys));
        Assert.Throws<ArgumentNullException>(() => ConfigModelValidator.Validate(EndpointOptions(), CompleteDocument(), null!));
    }

    private static TempWorkspace EndpointsWorkspace(JsonObject? config = null, string? document = null)
    {
        TempWorkspace workspace = TempWorkspace.CreateEmpty();
        workspace.WriteConfig(EndpointsFixtures.Text(config ?? EndpointsFixtures.DefaultConfig()));
        _ = workspace.WriteFile(EndpointsFixtures.DocumentPath, document ?? EndpointsFixtures.DefaultDocument());
        return workspace;
    }

    private static Task<CliRunResult> ValidateConfigAsync(TempWorkspace workspace, params string[] arguments) =>
        CliHarness.RunTokensAsync(["validate", .. arguments], new CliHostOverrides(WorkingDirectory: workspace.Root));

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_exits_0_and_prints_the_count_when_every_id_is_recorded()
    {
        using TempWorkspace workspace = EndpointsWorkspace();

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 4 model IDs"], OutputLines(result.Stdout));
        Assert.Empty(result.Stderr);
        Assert.Empty(result.DeniedRequests);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_exits_4_prints_every_problem_line_and_makes_no_request()
    {
        JsonObject config = EndpointsFixtures.DefaultConfig();
        EndpointsFixtures.TierOf(config, "nebius")["ModelId"] = "model-nebius-missing";
        EndpointsFixtures.SectionOf(config)["Judge"]!["ModelId"] = "judge-missing";
        using TempWorkspace workspace = EndpointsWorkspace(config);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(["Missing in section ## nebius: model-nebius-missing", "Missing in section ## nvidia: judge-missing"], OutputLines(result.Stdout));
        string error = Assert.Single(OutputLines(result.Stderr));
        Assert.StartsWith("error: ", error, StringComparison.Ordinal);
        Assert.EndsWith("(ModelNotFoundException)", error, StringComparison.Ordinal);
        Assert.Contains("model-nebius-missing", error, StringComparison.Ordinal);
        Assert.DoesNotContain("no models listed", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(result.DeniedRequests);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_exits_4_with_the_verified_line_for_an_enabled_tier_without_a_date()
    {
        string document = EndpointsFixtures.Document(
            EndpointsFixtures.Section("local", Verified, "model-local"),
            EndpointsFixtures.Section("nvidia", Verified, "model-nvidia"),
            EndpointsFixtures.Section("nebius", null, "model-nebius"));
        using TempWorkspace workspace = EndpointsWorkspace(document: document);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(["Verified date missing in section ## nebius"], OutputLines(result.Stdout));
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_skips_a_disabled_tier_whose_section_has_no_date_but_needs_its_heading()
    {
        JsonObject config = EndpointsFixtures.DefaultConfig();
        EndpointsFixtures.SectionOf(config)["Tiers"]!.AsObject()["cloudgpu"] = EndpointsFixtures.TierEntry("cloudgpu", "model-cloudgpu", enabled: false);
        string without = EndpointsFixtures.Document(EndpointsFixtures.DefaultSections());
        string with = EndpointsFixtures.Document([.. EndpointsFixtures.DefaultSections(), "## cloudgpu\nStatus: pending (verified in M5, AJ-126)\n"]);

        using TempWorkspace headingMissing = EndpointsWorkspace(config, without);
        using TempWorkspace headingPresent = EndpointsWorkspace(config, with);
        CliRunResult missing = await ValidateConfigAsync(headingMissing, "--config");
        CliRunResult present = await ValidateConfigAsync(headingPresent, "--config");

        Assert.Equal(4, missing.ExitCode);
        Assert.Equal(["Section ## cloudgpu missing"], OutputLines(missing.Stdout));
        Assert.Equal(0, present.ExitCode);
        Assert.Equal(["Validated 4 model IDs"], OutputLines(present.Stdout));
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_with_a_path_validates_the_named_file_instead_of_the_default()
    {
        JsonObject broken = EndpointsFixtures.DefaultConfig();
        EndpointsFixtures.TierOf(broken, "local")["ModelId"] = "model-local-missing";
        using TempWorkspace workspace = EndpointsWorkspace(broken);
        _ = workspace.WriteFile(Path.Combine("alt", "other.json"), EndpointsFixtures.Text(EndpointsFixtures.DefaultConfig()));

        CliRunResult named = await ValidateConfigAsync(workspace, "--config", Path.Combine("alt", "other.json"));
        CliRunResult flag = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(0, named.ExitCode);
        Assert.Equal(["Validated 4 model IDs"], OutputLines(named.Stdout));
        Assert.Equal(4, flag.ExitCode);
        Assert.Equal(["Missing in section ## local: model-local-missing"], OutputLines(flag.Stdout));
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_binds_the_environment_overrides_over_the_file()
    {
        using TempWorkspace workspace = EndpointsWorkspace();
        var overrides = new CliHostOverrides(
            Configuration: new Dictionary<string, string?> { ["AgentJoules:Tiers:local:ModelId"] = "model-from-the-environment" },
            WorkingDirectory: workspace.Root);

        CliRunResult result = await CliHarness.RunTokensAsync(["validate", "--config"], overrides);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(["Missing in section ## local: model-from-the-environment"], OutputLines(result.Stdout));
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_reads_the_document_named_by_Paths_Endpoints()
    {
        JsonObject config = EndpointsFixtures.DefaultConfig();
        EndpointsFixtures.SectionOf(config)["Paths"] = new JsonObject { ["Endpoints"] = "notes/recorded.md" };
        using TempWorkspace workspace = EndpointsWorkspace(config);
        _ = workspace.WriteFile(Path.Combine("notes", "recorded.md"), EndpointsFixtures.DefaultDocument());
        File.Delete(Path.Combine(workspace.Root, EndpointsFixtures.DocumentPath));

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 4 model IDs"], OutputLines(result.Stdout));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_exits_2_naming_Paths_Endpoints_when_the_document_is_missing_or_unreadable(bool unreadable)
    {
        using TempWorkspace workspace = EndpointsWorkspace();
        string path = Path.Combine(workspace.Root, EndpointsFixtures.DocumentPath);
        File.Delete(path);
        if (unreadable)
        {
            _ = Directory.CreateDirectory(path);
        }

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string error = Assert.Single(OutputLines(result.Stderr));
        Assert.Contains("Paths:Endpoints", error, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", error, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../endpoints.md")]
    [InlineData("/rooted.md")]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_exits_2_naming_Paths_Endpoints_when_the_path_leaves_the_working_directory(string value)
    {
        // The working directory is one level inside the workspace and a complete document sits one level up, so a path that were followed would validate: only the refusal gives exit 2.
        JsonObject config = EndpointsFixtures.DefaultConfig();
        EndpointsFixtures.SectionOf(config)["Paths"] = new JsonObject { ["Endpoints"] = value };
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("work", "config", "appsettings.json"), EndpointsFixtures.Text(config));
        _ = workspace.WriteFile("endpoints.md", EndpointsFixtures.DefaultDocument());
        Assert.True(File.Exists(Path.Combine(workspace.Root, "endpoints.md")));

        CliRunResult result = await CliHarness.RunTokensAsync(["validate", "--config"], new CliHostOverrides(WorkingDirectory: Path.Combine(workspace.Root, "work")));

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string error = Assert.Single(OutputLines(result.Stderr));
        Assert.Contains("Paths:Endpoints must be a relative path inside the working directory", error, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_exits_2_naming_Paths_Endpoints_and_not_the_path_when_the_named_document_does_not_exist()
    {
        JsonObject config = EndpointsFixtures.DefaultConfig();
        EndpointsFixtures.SectionOf(config)["Paths"] = new JsonObject { ["Endpoints"] = "notes/absent.md" };
        using TempWorkspace workspace = EndpointsWorkspace(config);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(2, result.ExitCode);
        string error = Assert.Single(OutputLines(result.Stderr));
        Assert.Contains("Paths:Endpoints: the endpoints document was not found", error, StringComparison.Ordinal);
        Assert.DoesNotContain("absent.md", error, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("runs")]
    [InlineData("results/20260101T000000Z-local.json")]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_with_a_positional_path_exits_2_and_checks_nothing(string path)
    {
        using TempWorkspace workspace = EndpointsWorkspace();

        CliRunResult result = await ValidateConfigAsync(workspace, path, "--config");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string error = Assert.Single(OutputLines(result.Stderr));
        Assert.Contains("validate --config takes no path argument", error, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_with_a_missing_configuration_file_exits_2_and_the_help_no_longer_defers_to_M4()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();

        CliRunResult missing = await ValidateConfigAsync(workspace, "--config", "no-such.json");
        CliRunResult help = await ValidateConfigAsync(workspace, "--help");

        Assert.Equal(2, missing.ExitCode);
        Assert.DoesNotContain("arrives in M4", missing.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("--config", help.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("M4", help.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Reserved", help.Stdout, StringComparison.Ordinal);
    }

    private const string LongIdStem = "hf.co/someorg/Some-Very-Long-Model-Name-GGUF:Q4_K_M-with-a-long-tail-variant-";

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void Two_missing_ids_that_share_their_first_64_characters_are_two_lines_each_ending_with_its_full_id()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["local"].ModelId = LongIdStem + "1";
        options.Tiers["local"].AgentModelIds["Planner"] = LongIdStem + "2";
        Assert.Equal(LongIdStem[..64], (LongIdStem + "2")[..64]);

        ConfigValidationResult result = ConfigModelValidator.Validate(options, CompleteDocument(), NoKeys);

        Assert.Equal([$"Missing in section ## local: {LongIdStem}1", $"Missing in section ## local: {LongIdStem}2"], result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void An_id_of_300_characters_is_cut_at_256_and_marked_with_the_ellipsis()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["local"].ModelId = new string('a', 300);

        ConfigValidationResult result = ConfigModelValidator.Validate(options, CompleteDocument(), NoKeys);

        Assert.Equal([$"Missing in section ## local: {new string('a', 256)}..."], result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void An_id_with_a_control_character_prints_the_character_as_a_question_mark()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["local"].ModelId = "bad\u0007id";

        ConfigValidationResult result = ConfigModelValidator.Validate(options, CompleteDocument(), NoKeys);

        Assert.Equal(["Missing in section ## local: bad?id"], result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public void The_same_tier_and_id_configured_as_model_agent_and_judge_prints_once()
    {
        AgentJoulesOptions options = EndpointOptions();
        options.Tiers["local"].ModelId = "shared-id";
        options.Tiers["local"].AgentModelIds["Planner"] = "shared-id";
        options.Judge.Tier = "local";
        options.Judge.ModelId = "shared-id";

        ConfigValidationResult result = ConfigModelValidator.Validate(options, CompleteDocument(), NoKeys);

        Assert.Equal(["Missing in section ## local: shared-id"], result.Problems);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task A_malformed_verified_date_prints_the_missing_line_and_then_the_malformed_line_and_exits_4()
    {
        string document = EndpointsFixtures.Document(
            EndpointsFixtures.Section("local", "24.10.2026", "model-local"),
            EndpointsFixtures.Section("nvidia", Verified, "model-nvidia"),
            EndpointsFixtures.Section("nebius", Verified, "model-nebius"));
        using TempWorkspace workspace = EndpointsWorkspace(document: document);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");
        ConfigValidationResult valid = ConfigModelValidator.Validate(EndpointOptions(), CompleteDocument(), NoKeys);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(["Verified date missing in section ## local", "Verified date malformed in section ## local: 24.10.2026"], OutputLines(result.Stdout));
        Assert.Empty(valid.Problems);
        Assert.DoesNotContain(valid.Problems, static line => line.Contains("malformed", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_exits_2_naming_Paths_Endpoints_for_a_document_of_one_MiB_plus_one_byte()
    {
        string head = EndpointsFixtures.DefaultDocument();
        using TempWorkspace workspace = EndpointsWorkspace(document: head + new string('p', (1024 * 1024) + 1 - head.Length));
        Assert.Equal((1024 * 1024) + 1, new FileInfo(Path.Combine(workspace.Root, EndpointsFixtures.DocumentPath)).Length);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string error = Assert.Single(OutputLines(result.Stderr));
        Assert.Contains("Paths:Endpoints: the endpoints document could not be read; the path is not shown", error, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_accepts_a_document_of_exactly_one_MiB()
    {
        string head = EndpointsFixtures.DefaultDocument();
        using TempWorkspace workspace = EndpointsWorkspace(document: head + new string('p', (1024 * 1024) - head.Length));
        Assert.Equal(1024 * 1024, new FileInfo(Path.Combine(workspace.Root, EndpointsFixtures.DocumentPath)).Length);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 4 model IDs"], OutputLines(result.Stdout));
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_exits_2_not_4_for_a_document_that_starts_with_a_UTF16_byte_order_mark()
    {
        using TempWorkspace workspace = EndpointsWorkspace();
        string path = Path.Combine(workspace.Root, EndpointsFixtures.DocumentPath);
        byte[] text = Encoding.Unicode.GetBytes(EndpointsFixtures.DefaultDocument());
        File.WriteAllBytes(path, [.. Encoding.Unicode.GetPreamble(), .. text]);
        Assert.Equal([0xFF, 0xFE], File.ReadAllBytes(path)[..2]);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Stdout);
        string error = Assert.Single(OutputLines(result.Stderr));
        Assert.Contains("Paths:Endpoints: the endpoints document could not be read; the path is not shown", error, StringComparison.Ordinal);
        Assert.EndsWith("(ConfigurationException)", error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Validate_config_accepts_a_UTF8_byte_order_mark_and_CRLF_line_ends_when_the_first_line_is_a_heading()
    {
        using TempWorkspace workspace = EndpointsWorkspace();
        string path = Path.Combine(workspace.Root, EndpointsFixtures.DocumentPath);
        string crlf = string.Concat(EndpointsFixtures.DefaultSections()).Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(crlf)]);
        Assert.StartsWith("## local", crlf, StringComparison.Ordinal);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Validated 4 model IDs"], OutputLines(result.Stdout));
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task Committed_configuration_validates_against_the_committed_endpoints_document()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        string committed = File.ReadAllText(Path.Combine(ConfigurationFixtures.RepositoryRoot(), "docs", "endpoints.md"));
        _ = workspace.WriteFile(EndpointsFixtures.DocumentPath, committed);

        CliRunResult result = await ValidateConfigAsync(workspace, "--config");

        Assert.Equal(0, result.ExitCode);
        string line = Assert.Single(OutputLines(result.Stdout));
        Assert.Matches(@"^Validated [0-9]+ model IDs$", line);
        Assert.Empty(result.DeniedRequests);
    }

    [Fact]
    [Trait("Spec", "REQ-018")]
    public async Task A_registered_key_inside_a_missing_id_is_masked_before_the_echo_is_cut()
    {
        // A runtime-built 236-character fake key: with the 30-character prefix the id is 266 characters, so a cut at 256 before the masking would show 226 characters of the key.
        const string alphabet = "BCDFGHJKLMNPQRSTVWXZ0123456789bcdfghjkmnpqrstvwxz";
        string key = string.Concat(Enumerable.Range(0, 236).Select(static index => alphabet[((index * 7) + (index / 11)) % alphabet.Length]));
        const string prefix = "ordinary-model-name-prefix-abc";
        Assert.Equal(30, prefix.Length);
        Assert.Equal(236, key.Length);
        JsonObject config = EndpointsFixtures.DefaultConfig();
        EndpointsFixtures.TierOf(config, "local")["ModelId"] = prefix + key;
        using TempWorkspace workspace = EndpointsWorkspace(config);

        CliRunResult result = await CliHarness.RunTokensAsync(
            ["validate", "--config"],
            new CliHostOverrides(Configuration: new Dictionary<string, string?> { [ProviderClientFixtures.NvidiaKeyVariable] = key }, WorkingDirectory: workspace.Root));

        Assert.Equal(4, result.ExitCode);
        Assert.Equal([$"Missing in section ## local: {prefix}****"], OutputLines(result.Stdout));
        for (int start = 0; start + 8 <= key.Length; start++)
        {
            string run = key.Substring(start, 8);
            Assert.False(result.Stdout.Contains(run, StringComparison.Ordinal) || result.Stderr.Contains(run, StringComparison.Ordinal), "a run of the key reached the output at offset " + start.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Contains($"Missing in section ## local: {prefix}****", Assert.Single(OutputLines(result.Stderr)), StringComparison.Ordinal);
    }

    // ---- AJ-102: Local:Runtime (REQ-019) ----

    [Fact]
    [Trait("Spec", "REQ-019")]
    public void Local_runtime_defaults_to_ollama_without_a_key()
    {
        Assert.Equal(LocalOptions.Ollama, new AgentJoulesOptions().Local.Runtime);
        Assert.Equal(LocalOptions.Ollama, OptionsLoader.Bind(ConfigurationFixtures.InMemory()).Local.Runtime);
        Assert.Equal(LocalOptions.Ollama, Load().Local.Runtime);
    }

    [Fact]
    [Trait("Spec", "REQ-019")]
    public void Local_runtime_binds_from_the_environment_variable_name()
    {
        IConfiguration configuration = ConfigurationFixtures.InMemory((ConfigurationFixtures.NormaliseEnvironmentKey("AGENTJOULES__Local__Runtime"), LocalOptions.Nim));

        Assert.Equal(LocalOptions.Nim, OptionsLoader.Load(configuration).Local.Runtime);
    }

    [Theory]
    [Trait("Spec", "REQ-019")]
    [InlineData("ollama")]
    [InlineData("nim")]
    public void Local_runtime_accepts_ollama_and_nim(string runtime) =>
        Assert.Equal(runtime, Load(("Local:Runtime", runtime)).Local.Runtime);

    [Theory]
    [Trait("Spec", "REQ-019")]
    [InlineData("vllm")]
    [InlineData("NIM")]
    [InlineData("nim ")]
    public void Local_runtime_other_than_ollama_or_nim_exits_2_without_showing_the_value(string runtime)
    {
        ConfigurationException failure = Rejected(("Local:Runtime", runtime));

        Assert.Equal(ExitCode.InvalidArguments, failure.ExitCode);
        Assert.Equal("Local:Runtime must be ollama or nim", failure.Message);
        Assert.DoesNotContain(runtime, failure.Message, StringComparison.Ordinal);
    }
}