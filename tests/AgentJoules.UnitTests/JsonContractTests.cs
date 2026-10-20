using System.Reflection;
using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// Guards the JSON contract of <see cref="ReadingFlags"/>. A <see cref="JsonStringEnumConverter"/> in the caller's options
/// outranks the converter attribute on the enum type, so every serialised member of that type must carry
/// <see cref="ReadingFlagsJsonConverter"/> itself, and a collection of flags cannot be fixed by an attribute at all.
/// </summary>
[Trait("Spec", "REQ-005")]
public sealed class JsonContractTests
{
    // Add the assembly of every project that holds serialised types (M2: the results file and the report) to this list.
    private static readonly Assembly[] SerialisedAssemblies = [typeof(EnergyReading).Assembly];

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool IsFlags(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(ReadingFlags);

    private static bool HasFlagsConverter(MemberInfo member) =>
        member.GetCustomAttributes<JsonConverterAttribute>().Any(a => a.ConverterType == typeof(ReadingFlagsJsonConverter));

    // An array or a sequence of flags; a nullable flags value is a plain member, not a collection.
    private static bool IsFlagsSequence(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is not null)
        {
            return false;
        }

        Type target = type.IsByRef ? type.GetElementType()! : type;
        return target.IsArray
            ? IsFlags(target.GetElementType()!)
            : (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(IEnumerable<>) && IsFlags(target.GetGenericArguments()[0]))
                || target.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && IsFlags(i.GetGenericArguments()[0]));
    }

    // A flags value wrapped in any generic type, such as a dictionary of flags; checked on serialised members only.
    private static bool WrapsFlags(Type type) =>
        Nullable.GetUnderlyingType(type) is null && type.IsGenericType && type.GetGenericArguments().Any(a => IsFlags(a) || WrapsFlags(a));

    internal static IEnumerable<string> Violations(IEnumerable<Type> types)
    {
        foreach (Type type in types.Where(t => !t.IsEnum))
        {
            foreach (PropertyInfo property in type.GetProperties(Declared).Where(p => !(p.GetMethod ?? p.SetMethod)!.IsStatic))
            {
                if (IsFlags(property.PropertyType) && !HasFlagsConverter(property))
                {
                    yield return $"{type.FullName}.{property.Name}: a ReadingFlags property needs [JsonConverter(typeof(ReadingFlagsJsonConverter))].";
                }

                if (IsFlagsSequence(property.PropertyType) || WrapsFlags(property.PropertyType))
                {
                    yield return $"{type.FullName}.{property.Name}: a collection of ReadingFlags cannot be serialised as vocabulary strings.";
                }
            }

            foreach (FieldInfo field in type.GetFields(Declared).Where(f => !f.IsStatic))
            {
                if (IsFlags(field.FieldType) && !HasFlagsConverter(field))
                {
                    yield return $"{type.FullName}.{field.Name}: a ReadingFlags field needs [JsonConverter(typeof(ReadingFlagsJsonConverter))].";
                }

                if (IsFlagsSequence(field.FieldType) || WrapsFlags(field.FieldType))
                {
                    yield return $"{type.FullName}.{field.Name}: a collection of ReadingFlags cannot be serialised as vocabulary strings.";
                }
            }

            foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    PropertyInfo? matching = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));

                    if (IsFlags(parameter.ParameterType) && (matching is null || !HasFlagsConverter(matching)))
                    {
                        yield return $"{type.FullName}..ctor({parameter.Name}): a ReadingFlags parameter needs a property of the same name with [JsonConverter(typeof(ReadingFlagsJsonConverter))].";
                    }

                    if (IsFlagsSequence(parameter.ParameterType) || WrapsFlags(parameter.ParameterType))
                    {
                        yield return $"{type.FullName}..ctor({parameter.Name}): a collection of ReadingFlags cannot be serialised as vocabulary strings.";
                    }
                }
            }

            foreach (MethodInfo method in type.GetMethods(Declared))
            {
                if (IsFlagsSequence(method.ReturnType) || method.GetParameters().Any(p => IsFlagsSequence(p.ParameterType)))
                {
                    yield return $"{type.FullName}.{method.Name}: a collection of ReadingFlags in a signature cannot be serialised as vocabulary strings.";
                }
            }
        }
    }

    [Fact]
    public void EveryReadingFlagsMemberOfTheSerialisedAssembliesCarriesItsConverterAndNoCollectionOfFlagsExists()
    {
        Assert.NotEmpty(SerialisedAssemblies);

        Assert.Empty(SerialisedAssemblies.SelectMany(assembly => Violations(assembly.GetExportedTypes())));
    }

    [Fact]
    public void TheScanSeesTheFlagsMemberOfEnergyReadingAndAcceptsItsConverter()
    {
        // A positive control: the scan reaches the type that has the member, and the member passes only because of its attribute.
        PropertyInfo flags = typeof(EnergyReading).GetProperty(nameof(EnergyReading.Flags))!;

        Assert.True(IsFlags(flags.PropertyType));
        Assert.True(HasFlagsConverter(flags));
        Assert.Contains(typeof(EnergyReading), typeof(EnergyReading).Assembly.GetExportedTypes());
        Assert.Empty(Violations([typeof(EnergyReading)]));
    }

    [Theory]
    [InlineData(typeof(PlainFlagsProperty))]
    [InlineData(typeof(NullableFlagsProperty))]
    [InlineData(typeof(PlainFlagsField))]
    [InlineData(typeof(FlagsListProperty))]
    [InlineData(typeof(FlagsArrayProperty))]
    [InlineData(typeof(FlagsDictionaryProperty))]
    [InlineData(typeof(FlagsConstructorParameter))]
    [InlineData(typeof(FlagsCollectionMethod))]
    public void TheScanFlagsAMemberThatBreaksTheContract(Type fixture)
    {
        Assert.NotEmpty(Violations([fixture]));
    }

    [Theory]
    [InlineData(typeof(ConvertedFlagsProperty))]
    [InlineData(typeof(ConvertedFlagsConstructor))]
    [InlineData(typeof(UnrelatedMembers))]
    public void TheScanAcceptsAMemberThatKeepsTheContract(Type fixture)
    {
        Assert.Empty(Violations([fixture]));
    }

    public sealed class PlainFlagsProperty
    {
        public ReadingFlags Flags { get; set; }
    }

    public sealed class NullableFlagsProperty
    {
        public ReadingFlags? Flags { get; set; }
    }

    public sealed class PlainFlagsField
    {
#pragma warning disable CA1051 // The public field is the point of this fixture.
        public ReadingFlags Flags;
#pragma warning restore CA1051
    }

    public sealed class FlagsListProperty
    {
        [JsonConverter(typeof(ReadingFlagsJsonConverter))]
        public IReadOnlyList<ReadingFlags> Flags { get; } = [];
    }

    public sealed class FlagsArrayProperty
    {
        public ReadingFlags[] Flags { get; } = [];
    }

    public sealed class FlagsDictionaryProperty
    {
        public Dictionary<string, ReadingFlags> Flags { get; } = [];
    }

    public sealed class FlagsConstructorParameter(ReadingFlags flags)
    {
        public int Value { get; } = (int)flags;
    }

    public sealed class FlagsCollectionMethod
    {
        public static IEnumerable<ReadingFlags> Enumerate() => [];
    }

    public sealed class ConvertedFlagsProperty
    {
        [JsonConverter(typeof(ReadingFlagsJsonConverter))]
        public ReadingFlags Flags { get; set; }
    }

    public sealed class ConvertedFlagsConstructor(ReadingFlags flags)
    {
        [JsonConverter(typeof(ReadingFlagsJsonConverter))]
        public ReadingFlags Flags { get; } = flags;
    }

    public sealed class UnrelatedMembers
    {
        public static ReadingFlags Static { get; set; }

        public string Name { get; set; } = string.Empty;

        public IReadOnlyList<string> Names { get; } = [];

        public static string Describe(ReadingFlags flags) => flags.ToString();
    }
}