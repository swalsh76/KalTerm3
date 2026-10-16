using System.Reflection;
using KalTerm.Core.Money;

namespace KalTerm.Core.Tests.Money;

/// <summary>Money is never a float or double (CLAUDE.md): enforced on the public surface of the money types.</summary>
public class NoFloatingPointApiTests
{
    public static TheoryData<Type> MoneyTypes() => new() { typeof(Dollars), typeof(Contracts) };

    [Theory]
    [MemberData(nameof(MoneyTypes))]
    public void NoPublicMemberTakesOrReturnsAFloatingPointType(Type type)
    {
        var offenders = new List<string>();
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var types = member switch
            {
                MethodBase method => method.GetParameters().Select(p => p.ParameterType)
                    .Append(method is MethodInfo info ? info.ReturnType : typeof(void)),
                PropertyInfo property => [property.PropertyType],
                FieldInfo field => [field.FieldType],
                _ => [],
            };

            if (types.Any(t => t == typeof(double) || t == typeof(float) || t == typeof(Half)))
            {
                offenders.Add(member.ToString()!);
            }
        }

        Assert.Empty(offenders);
    }
}
