using System.Reflection;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Services.Runtime;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Project_references_follow_the_approved_dependency_direction()
    {
        var references = ProjectAssemblies().ToArray();

        Assert.DoesNotContain(references, value =>
            value.Source == "ErpGenerator.Runtime.Shared" &&
            value.Target is "ErpGenerator.Runtime.Data" or "ErpGenerator.Runtime.Services" or "ErpGenerator.Runtime.WebHost");
        Assert.DoesNotContain(references, value =>
            value.Source == "ErpGenerator.Runtime.Data" &&
            value.Target is "ErpGenerator.Runtime.Services" or "ErpGenerator.Runtime.WebHost");
        Assert.DoesNotContain(references, value =>
            value.Source == "ErpGenerator.Runtime.Services" && value.Target == "ErpGenerator.Runtime.WebHost");
        Assert.DoesNotContain(references, value =>
            value.Source == "ErpGenerator.Runtime.WebHost" && value.Target == "ErpGenerator.Runtime.Data");
    }

    [Fact]
    public void Service_interfaces_do_not_expose_data_layer_types()
    {
        var servicesAssembly = typeof(IRuntimeRecordService).Assembly;
        var dataAssemblyName = typeof(RuntimeDatabase).Assembly.GetName().Name;

        var exposedDataTypes = servicesAssembly.ExportedTypes
            .Where(type => type.IsInterface)
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(MemberTypes)
            .SelectMany(ExpandTypes)
            .Where(type => type.Assembly.GetName().Name == dataAssemblyName)
            .Distinct()
            .ToArray();

        Assert.Empty(exposedDataTypes);
    }

    [Fact]
    public void Service_interfaces_do_not_expose_web_framework_types()
    {
        var exposedWebTypes = typeof(IRuntimeRecordService).Assembly.ExportedTypes
            .Where(type => type.IsInterface)
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(MemberTypes)
            .SelectMany(ExpandTypes)
            .Where(type => type.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true)
            .Distinct()
            .ToArray();

        Assert.Empty(exposedWebTypes);
    }

    private static IEnumerable<(string Source, string Target)> ProjectAssemblies()
    {
        var assemblies = new[]
        {
            typeof(ResolvedTenantEnvironment).Assembly,
            typeof(RuntimeDatabase).Assembly,
            typeof(IRuntimeRecordService).Assembly,
            Assembly.Load("ErpGenerator.Runtime.WebHost")
        };

        return assemblies.SelectMany(source => source.GetReferencedAssemblies()
            .Where(target => target.Name?.StartsWith("ErpGenerator.Runtime.", StringComparison.Ordinal) == true)
            .Select(target => (source.GetName().Name!, target.Name!)));
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        MethodInfo method => method.GetParameters().Select(value => value.ParameterType).Append(method.ReturnType),
        ConstructorInfo constructor => constructor.GetParameters().Select(value => value.ParameterType),
        _ => []
    };

    private static IEnumerable<Type> ExpandTypes(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in ExpandTypes(element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in ExpandTypes(argument))
            {
                yield return nested;
            }
        }
    }
}
