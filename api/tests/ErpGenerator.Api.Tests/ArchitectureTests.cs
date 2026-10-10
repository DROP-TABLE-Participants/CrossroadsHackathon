using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ErpGenerator.Api.Shared.Identity;

namespace ErpGenerator.Api.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Project_references_follow_the_approved_dependency_direction()
    {
        Assert.DoesNotContain(ProjectAssemblies(), value =>
            value.Source == "ErpGenerator.Api.Shared" && value.Target != "ErpGenerator.Api.Shared");
        Assert.DoesNotContain(ProjectAssemblies(), value =>
            value.Source == "ErpGenerator.Api.Data" && value.Target is "ErpGenerator.Api.Services" or "ErpGenerator.Api.WebHost");
        Assert.DoesNotContain(ProjectAssemblies(), value =>
            value.Source == "ErpGenerator.Api.Services" && value.Target == "ErpGenerator.Api.WebHost");
        Assert.DoesNotContain(ProjectAssemblies(), value =>
            value.Source == "ErpGenerator.Api.WebHost" && value.Target == "ErpGenerator.Api.Data");
        Assert.DoesNotContain(ProjectAssemblies(), value =>
            value.Source == "ErpGenerator.Api.MigrationService" &&
            value.Target is "ErpGenerator.Api.Data" or "ErpGenerator.Api.WebHost");
    }

    [Fact]
    public void Shared_contracts_do_not_expose_data_entities()
    {
        var sharedAssembly = typeof(CustomerViewModel).Assembly;
        var dataAssemblyName = typeof(ErpGeneratorDbContext).Assembly.GetName().Name;

        var exposedTypes = sharedAssembly.ExportedTypes
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(MemberTypes)
            .SelectMany(ExpandTypes)
            .Where(type => type.Assembly.GetName().Name == dataAssemblyName)
            .ToArray();

        Assert.Empty(exposedTypes);
    }

    [Fact]
    public void Service_interfaces_do_not_expose_data_entities()
    {
        var servicesAssembly = typeof(ICustomerService).Assembly;
        var dataAssemblyName = typeof(ErpGeneratorDbContext).Assembly.GetName().Name;

        var exposedTypes = servicesAssembly.ExportedTypes
            .Where(type => type.IsInterface)
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(MemberTypes)
            .SelectMany(ExpandTypes)
            .Where(type => type.Assembly.GetName().Name == dataAssemblyName)
            .ToArray();

        Assert.Empty(exposedTypes);
    }

    [Fact]
    public void Services_do_not_introduce_repository_abstractions()
    {
        var serviceTypes = typeof(ICustomerService).Assembly.ExportedTypes;
        Assert.DoesNotContain(serviceTypes, type => type.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Protected_web_endpoints_always_name_an_identity_realm_policy()
    {
        var webAssembly = Assembly.Load("ErpGenerator.Api.WebHost");
        var authorizeAttributes = webAssembly.ExportedTypes
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) ||
                           type.Name.EndsWith("Hub", StringComparison.Ordinal))
            .SelectMany(type => type.GetCustomAttributes<AuthorizeAttribute>()
                .Concat(type.GetMethods().SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>())))
            .ToArray();

        Assert.NotEmpty(authorizeAttributes);
        Assert.All(authorizeAttributes, attribute =>
            Assert.Contains(
                attribute.Policy,
                new[] { "CustomerPortalUser", "PlatformAdministrator" }));
    }

    private static IEnumerable<(string Source, string Target)> ProjectAssemblies()
    {
        var assemblies = new[]
        {
            typeof(CustomerViewModel).Assembly,
            typeof(ErpGeneratorDbContext).Assembly,
            typeof(ICustomerService).Assembly,
            Assembly.Load("ErpGenerator.Api.WebHost"),
            Assembly.Load("ErpGenerator.Api.MigrationService")
        };

        return assemblies.SelectMany(source => source.GetReferencedAssemblies()
            .Where(target => target.Name?.StartsWith("ErpGenerator.Api.", StringComparison.Ordinal) == true)
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

        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var expanded in ExpandTypes(elementType)) yield return expanded;
        }

        foreach (var genericArgument in type.GetGenericArguments())
        {
            foreach (var expanded in ExpandTypes(genericArgument)) yield return expanded;
        }

        if (!type.IsGenericParameter) yield break;
        foreach (var constraint in type.GetGenericParameterConstraints())
        {
            foreach (var expanded in ExpandTypes(constraint)) yield return expanded;
        }
    }
}
