using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Configuration;
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
}