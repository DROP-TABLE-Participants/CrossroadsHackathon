using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.NodeDeserializers;
using YamlDotNet.RepresentationModel;
using ErpGenerator.Runtime.Shared.Bundles;

namespace ErpGenerator.Runtime.Shared.Compilation;

public sealed class ErpBundleYamlLoader
{
    private readonly IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithAttemptingUnquotedStringTypeDeserialization()
        .WithNodeDeserializer(
            new LosslessObjectNumericScalarNodeDeserializer(),
            location => location.Before<ScalarNodeDeserializer>())
        .IgnoreUnmatchedProperties()
        .Build();

    // Acceptance contracts are security- and promotion-relevant executable
    // input. Unlike legacy bundle metadata, unknown fields must never be
    // silently accepted as if they contributed to the canonical contract.
    private readonly IDeserializer strictAcceptanceDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithAttemptingUnquotedStringTypeDeserialization()
        .WithNodeDeserializer(
            new LosslessObjectNumericScalarNodeDeserializer(),
            location => location.Before<ScalarNodeDeserializer>())
        .Build();

    public ErpBundle Load(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new ArgumentException("Bundle YAML is required.", nameof(yaml));
        }

        ValidateTypedSecurityShapes(yaml);
        return deserializer.Deserialize<ErpBundle>(yaml);
    }

    public async Task<ErpBundle> LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        return Load(yaml);
    }

    public Task<ErpBundle> LoadPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(path))
        {
            return LoadDirectoryAsync(path, cancellationToken);
        }

        return LoadFileAsync(path, cancellationToken);
    }

    public async Task<ErpBundle> LoadDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Bundle directory '{path}' was not found.");
        }

        var manifestPath = Path.Combine(root, "manifest.yaml");
        if (!File.Exists(manifestPath))
        {
            throw new BundleCompilationException([$"{DisplayPath(manifestPath)} is required for folder bundles."]);
        }

        var errors = new List<string>();
        var manifest = await LoadYamlFileAsync<BundleManifestFile>(manifestPath, cancellationToken);
        var bundle = new ErpBundle
        {
            Id = manifest.Bundle.Id,
            Name = manifest.Bundle.Name,
            Version = manifest.Bundle.Version,
            Description = manifest.Bundle.Description,
            Modules = manifest.Modules
                .Select(module => new ErpModule { Id = module.Id, Name = module.Name })
                .ToList()
        };

        var modules = bundle.Modules.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        if (modules.Count == 0)
        {
            errors.Add($"{DisplayPath(manifestPath)} must declare at least one module.");
        }

        var moduleSources = modules.Keys.ToDictionary(id => id, _ => DisplayPath(manifestPath), StringComparer.OrdinalIgnoreCase);
        var entitySources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var workflowSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var viewSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var actionSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scheduleSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reportSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roleSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateYamlFiles(root, "entities"))
        {
            var document = await LoadYamlFileAsync<BundleEntityFile>(file, cancellationToken);
            var module = ResolveModule(document.Module, modules, file, errors);
            if (module is null)
            {
                continue;
            }

            AddUnique(document.Entity.Id, DisplayPath(file), entitySources, "entity", errors);
            module.Entities.Add(document.Entity);
        }

        foreach (var file in EnumerateYamlFiles(root, "views"))
        {
            var document = await LoadYamlFileAsync<BundleViewFile>(file, cancellationToken);
            var module = ResolveModule(document.Module, modules, file, errors);
            if (module is null)
            {
                continue;
            }

            AddUnique(document.View.Id, DisplayPath(file), viewSources, "view", errors);
            module.Views.Add(document.View);
        }

        foreach (var file in EnumerateYamlFiles(root, "workflows"))
        {
            var document = await LoadYamlFileAsync<BundleWorkflowFile>(file, cancellationToken);
            var module = ResolveModule(document.Module, modules, file, errors);
            if (module is null)
            {
                continue;
            }

            AddUnique(document.Workflow.Id, DisplayPath(file), workflowSources, "workflow", errors);
            module.Workflows.Add(document.Workflow);
        }

        foreach (var file in EnumerateYamlFiles(root, "actions"))
        {
            var document = await LoadYamlFileAsync<BundleActionFile>(file, cancellationToken);
            AddUnique(document.Action.Key, DisplayPath(file), actionSources, "action", errors);
            bundle.Actions.Add(document.Action);
        }

        foreach (var file in EnumerateYamlFiles(root, "schedules"))
        {
            var document = await LoadYamlFileAsync<BundleScheduleFile>(file, cancellationToken);
            AddUnique(document.Schedule.Id, DisplayPath(file), scheduleSources, "schedule", errors);
            bundle.Schedules.Add(document.Schedule);
        }

        foreach (var file in EnumerateYamlFiles(root, "reports"))
        {
            var document = await LoadYamlFileAsync<BundleReportFile>(file, cancellationToken);
            AddUnique(document.Report.Key, DisplayPath(file), reportSources, "report", errors);
            bundle.Reports.Add(document.Report);
        }

        foreach (var file in EnumerateYamlFiles(root, "dashboards"))
        {
            var document = await LoadYamlFileAsync<BundleDashboardFile>(file, cancellationToken);
            bundle.Dashboard.Widgets.AddRange(document.Dashboard.Widgets);
        }

        foreach (var file in EnumerateYamlFiles(root, "permissions"))
        {
            var document = await LoadYamlFileAsync<BundleRolesFile>(file, cancellationToken);
            foreach (var (roleId, role) in document.Roles)
            {
                AddUnique(roleId, DisplayPath(file), roleSources, "role", errors);
                bundle.Roles[roleId] = role;
            }
        }

        CanonicalizeDefinitionOrder(bundle);
        ValidateFolderReferences(
            bundle,
            entitySources,
            workflowSources,
            viewSources,
            actionSources,
            scheduleSources,
            reportSources,
            roleSources,
            errors);
        if (errors.Count > 0)
        {
            throw new BundleCompilationException(errors);
        }

        return bundle;
    }

    private static void CanonicalizeDefinitionOrder(ErpBundle bundle)
    {
        foreach (var module in bundle.Modules)
        {
            module.Entities.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
            module.Views.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
            module.Workflows.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        }

        bundle.Actions.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        bundle.Schedules.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        bundle.Reports.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));

        var roles = bundle.Roles.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        bundle.Roles.Clear();
        foreach (var (roleId, role) in roles)
        {
            bundle.Roles.Add(roleId, role);
        }
    }

    public Task<BundleSeedDocument> LoadSeedFileAsync(string path, CancellationToken cancellationToken = default) =>
        LoadYamlFileAsync<BundleSeedDocument>(path, cancellationToken);

    public async Task<IReadOnlyList<BundleAcceptanceTestDocument>> LoadAcceptanceTestsFromDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        var testsDirectory = Path.Combine(root, "tests");
        if (!Directory.Exists(testsDirectory))
        {
            return [];
        }

        var tests = new List<BundleAcceptanceTestDocument>();
        foreach (var file in EnumerateYamlFiles(root, "tests"))
        {
            var test = await LoadStrictAcceptanceTestFileAsync(file, cancellationToken);
            test.SourcePath = DisplayPath(file);
            tests.Add(test);
        }

        if (tests.Count == 0)
        {
            return [];
        }

        try
        {
            var binding = BundleAcceptanceTestContractHasher.Verify(tests);
            return binding.Tests.Select(test => test.Document).ToArray();
        }
        catch (BundleAcceptanceTestContractException exception)
        {
            throw new BundleCompilationException([exception.Message]);
        }
    }

    public async Task<BundleSeedDocument?> LoadSeedFromDirectoryAsync(
        string path,
        string seedKey,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        var seedPath = Path.Combine(root, "seeds", $"{seedKey}.yaml");
        if (!File.Exists(seedPath))
        {
            return null;
        }

        return await LoadSeedFileAsync(seedPath, cancellationToken);
    }

    private async Task<T> LoadYamlFileAsync<T>(string path, CancellationToken cancellationToken)
    {
        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new BundleCompilationException([$"{DisplayPath(path)} is empty."]);
        }

        try
        {
            ValidateTypedSecurityShapes(yaml);
            return deserializer.Deserialize<T>(yaml);
        }
        catch (BundleCompilationException ex)
        {
            throw new BundleCompilationException(ex.Errors.Select(error =>
                $"{DisplayPath(path)} could not be parsed: {error}").ToArray());
        }
        catch (Exception ex)
        {
            throw new BundleCompilationException([$"{DisplayPath(path)} could not be parsed: {ex.Message}"]);
        }
    }

    private static void ValidateTypedSecurityShapes(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        foreach (var document in stream.Documents)
        {
            ValidateRoleGrantRecordScopes(document.RootNode);
            ValidateFieldLifecycleShapes(document.RootNode);
            ValidateScheduleShapes(document.RootNode);
        }
    }

    private static void ValidateScheduleShapes(YamlNode node)
    {
        if (node is not YamlMappingNode document)
        {
            return;
        }

        if (TryGetMappingMember(document, "schedules", out var schedulesNode))
        {
            if (schedulesNode is not YamlSequenceNode schedules)
            {
                throw new BundleCompilationException(["schedules must be a sequence of typed schedule mappings."]);
            }

            foreach (var schedule in schedules.Children)
            {
                ValidateScheduleMapping(schedule);
            }
        }

        var hasScheduleWrapper = TryGetMappingMember(document, "schedule", out var scheduleNode);
        if (hasScheduleWrapper)
        {
            if (scheduleNode is not YamlMappingNode)
            {
                throw new BundleCompilationException(["Folder schedule wrapper must contain one typed schedule mapping."]);
            }

            foreach (var keyNode in document.Children.Keys)
            {
                if (keyNode is not YamlScalarNode keyScalar ||
                    NormalizeYamlKey(keyScalar.Value) != "schedule")
                {
                    throw new BundleCompilationException(
                        ["Folder schedule files must contain exactly one schedule wrapper member."]);
                }
            }

            ValidateScheduleMapping(scheduleNode!);
        }

        else
        {
            foreach (var keyNode in document.Children.Keys.OfType<YamlScalarNode>())
            {
                var key = NormalizeYamlKey(keyNode.Value);
                if (key is not ("schedule" or "schedules") &&
                    (key.Contains("schedule", StringComparison.Ordinal) ||
                     LevenshteinDistance(key, "schedules") <= 2))
                {
                    throw new BundleCompilationException(
                        [$"Bundle contains unsupported schedule-like member '{keyNode.Value}'; use exact member 'schedules'."]);
                }
            }
        }
    }

    private static void ValidateScheduleMapping(YamlNode node)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw new BundleCompilationException(["Schedule definitions must be typed mappings."]);
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "entity", "duefield", "filters", "workflow"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode keyScalar)
            {
                throw new BundleCompilationException(["Schedule member names must be scalar values."]);
            }

            var key = NormalizeYamlKey(keyScalar.Value);
            if (!allowed.Contains(key))
            {
                throw new BundleCompilationException(
                    [$"Schedule contains unsupported member '{keyScalar.Value}'; cron, SQL, timezone, action targets, and arbitrary expressions are not supported."]);
            }

            if (!seen.Add(key))
            {
                throw new BundleCompilationException([$"Schedule contains duplicate member '{keyScalar.Value}'."]);
            }

            if (key == "filters")
            {
                ValidateScheduleFilters(valueNode);
            }
            else if (valueNode is not YamlScalarNode)
            {
                throw new BundleCompilationException([$"Schedule member '{keyScalar.Value}' must be a scalar value."]);
            }
        }
    }

    private static void ValidateScheduleFilters(YamlNode node)
    {
        if (node is not YamlSequenceNode filters)
        {
            throw new BundleCompilationException(["Schedule filters must be a sequence of typed filter mappings."]);
        }

        foreach (var filterNode in filters.Children)
        {
            if (filterNode is not YamlMappingNode filter)
            {
                throw new BundleCompilationException(["Schedule filters must be typed mappings."]);
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "field", "op", "value", "values" };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (keyNode, valueNode) in filter.Children)
            {
                if (keyNode is not YamlScalarNode keyScalar)
                {
                    throw new BundleCompilationException(["Schedule filter member names must be scalar values."]);
                }

                var key = NormalizeYamlKey(keyScalar.Value);
                if (!allowed.Contains(key))
                {
                    throw new BundleCompilationException(
                        [$"Schedule filter contains unsupported member '{keyScalar.Value}'; only same-record typed literals are supported."]);
                }

                if (!seen.Add(key))
                {
                    throw new BundleCompilationException([$"Schedule filter contains duplicate member '{keyScalar.Value}'."]);
                }

                if (key == "value")
                {
                    ValidateScheduleLiteral(valueNode);
                }
                else if (key == "values")
                {
                    if (valueNode is not YamlSequenceNode values)
                    {
                        throw new BundleCompilationException(["Schedule filter values must be a sequence of typed literals."]);
                    }

                    foreach (var value in values.Children)
                    {
                        ValidateScheduleLiteral(value);
                    }
                }
                else if (valueNode is not YamlScalarNode)
                {
                    throw new BundleCompilationException([$"Schedule filter member '{keyScalar.Value}' must be a scalar value."]);
                }
            }
        }
    }

    private static void ValidateScheduleLiteral(YamlNode node)
    {
        if (node is not YamlMappingNode literal)
        {
            throw new BundleCompilationException(["Schedule filter literals must be mappings with exact type and value members."]);
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal) { "type", "value" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in literal.Children)
        {
            if (keyNode is not YamlScalarNode keyScalar)
            {
                throw new BundleCompilationException(["Schedule literal member names must be scalar values."]);
            }

            var key = NormalizeYamlKey(keyScalar.Value);
            if (!allowed.Contains(key) || !seen.Add(key) || valueNode is not YamlScalarNode)
            {
                throw new BundleCompilationException(
                    [$"Schedule filter literal contains an unsupported, duplicate, or non-scalar member '{keyScalar.Value}'."]);
            }
        }

        if (!seen.SetEquals(allowed))
        {
            throw new BundleCompilationException(["Schedule filter literals must declare exactly type and value."]);
        }
    }

    private static void ValidateFieldLifecycleShapes(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                if (TryGetMappingMember(mapping, "entities", out var entitiesNode) &&
                    entitiesNode is YamlSequenceNode entities)
                {
                    foreach (var nestedEntityMapping in entities.Children.OfType<YamlMappingNode>())
                    {
                        ValidateEntityFieldLifecycleShapes(nestedEntityMapping);
                    }
                }

                // Folder bundles wrap one entity definition in an `entity`
                // mapping. Action steps use scalar entity identifiers, so this
                // branch cannot classify action metadata as an entity field list.
                if (TryGetMappingMember(mapping, "entity", out var entityNode) &&
                    entityNode is YamlMappingNode entityMapping)
                {
                    ValidateEntityFieldLifecycleShapes(entityMapping);
                }

                foreach (var valueNode in mapping.Children.Values)
                {
                    ValidateFieldLifecycleShapes(valueNode);
                }
                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    ValidateFieldLifecycleShapes(child);
                }
                break;
        }
    }

    private static void ValidateEntityFieldLifecycleShapes(YamlMappingNode entity)
    {
        if (!TryGetMappingMember(entity, "fields", out var fieldsNode) ||
            fieldsNode is not YamlSequenceNode fields)
        {
            return;
        }

        foreach (var field in fields.Children.OfType<YamlMappingNode>())
        {
            ValidateFieldLifecycleMapping(field);
        }
    }

    private static void ValidateFieldLifecycleMapping(YamlMappingNode field)
    {
        YamlNode? lifecycleNode = null;
        var hasLifecycle = false;
        foreach (var (keyNode, valueNode) in field.Children)
        {
            if (keyNode is not YamlScalarNode keyScalar)
            {
                continue;
            }

            var key = NormalizeYamlKey(keyScalar.Value);
            if (key == "lifecycle")
            {
                if (hasLifecycle)
                {
                    throw new BundleCompilationException(
                        [$"Field contains duplicate lifecycle member '{keyScalar.Value}'."]);
                }

                hasLifecycle = true;
                lifecycleNode = valueNode;
                continue;
            }

            if (LooksLikeLifecycleKey(key))
            {
                throw new BundleCompilationException(
                    [$"Field contains unsupported lifecycle-like member '{keyScalar.Value}'; use exact member 'lifecycle'."]);
            }
        }

        if (!hasLifecycle)
        {
            return;
        }

        if (lifecycleNode is not YamlMappingNode lifecycle)
        {
            throw new BundleCompilationException(
                ["Field lifecycle must be a typed mapping with initial and transitions members."]);
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "initial",
            "transitions"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in lifecycle.Children)
        {
            if (keyNode is not YamlScalarNode keyScalar)
            {
                throw new BundleCompilationException(
                    ["Field lifecycle keys must be scalar initial or transitions names."]);
            }

            var key = NormalizeYamlKey(keyScalar.Value);
            if (!allowed.Contains(key))
            {
                throw new BundleCompilationException(
                    [$"Field lifecycle contains unsupported member '{keyScalar.Value}'; only initial and transitions are allowed."]);
            }

            if (!seen.Add(key))
            {
                throw new BundleCompilationException(
                    [$"Field lifecycle contains duplicate member '{keyScalar.Value}'."]);
            }

            if (valueNode is not YamlScalarNode)
            {
                throw new BundleCompilationException(
                    [$"Field lifecycle member '{keyScalar.Value}' must be a scalar value."]);
            }
        }
    }

    private static bool LooksLikeLifecycleKey(string key) =>
        key.Contains("lifecycle", StringComparison.Ordinal) ||
        key.Length > 0 && LevenshteinDistance(key, "lifecycle") <= 2;

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1] +
                                   (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void ValidateRoleGrantRecordScopes(YamlNode node)
    {
        if (node is not YamlMappingNode document ||
            !TryGetMappingMember(document, "roles", out var rolesNode) ||
            rolesNode is not YamlMappingNode roles)
        {
            return;
        }

        foreach (var roleNode in roles.Children.Values)
        {
            if (roleNode is not YamlMappingNode role ||
                !TryGetMappingMember(role, "grants", out var grantsNode) ||
                grantsNode is not YamlSequenceNode grants)
            {
                continue;
            }

            foreach (var grantNode in grants.Children)
            {
                if (grantNode is not YamlMappingNode grant)
                {
                    continue;
                }

                var hasRecordScope = TryGetMappingMember(grant, "recordscope", out var recordScopeNode);
                var isRecordGrant = TryGetMappingMember(grant, "scope", out var scopeNode) &&
                                    scopeNode is YamlScalarNode scopeScalar &&
                                    string.Equals(scopeScalar.Value?.Trim(), "record", StringComparison.OrdinalIgnoreCase);
                if (!hasRecordScope && !isRecordGrant)
                {
                    continue;
                }

                ValidateRecordGrantMapping(grant);
                if (hasRecordScope)
                {
                    ValidateRecordScopeMapping(recordScopeNode!);
                }
            }
        }
    }

    private static bool TryGetMappingMember(
        YamlMappingNode mapping,
        string normalizedName,
        out YamlNode? value)
    {
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is YamlScalarNode keyScalar &&
                NormalizeYamlKey(keyScalar.Value) == normalizedName)
            {
                value = valueNode;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static void ValidateRecordGrantMapping(YamlMappingNode mapping)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "scope",
            "action",
            "entity",
            "target",
            "recordscope"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode keyScalar)
            {
                throw new BundleCompilationException(
                    ["Record-scoped grant keys must be scalar scope, action, entity, target, or recordScope names."]);
            }

            var key = NormalizeYamlKey(keyScalar.Value);
            if (!allowed.Contains(key))
            {
                throw new BundleCompilationException(
                    [$"Record-scoped grant contains unsupported member '{keyScalar.Value}'; raw predicates, where clauses, and SQL are not supported."]);
            }

            if (!seen.Add(key))
            {
                throw new BundleCompilationException(
                    [$"Record-scoped grant contains duplicate member '{keyScalar.Value}'."]);
            }

            if (key != "recordscope" && valueNode is not YamlScalarNode)
            {
                throw new BundleCompilationException(
                    [$"Record-scoped grant member '{keyScalar.Value}' must be a scalar value."]);
            }
        }
    }

    private static void ValidateRecordScopeMapping(YamlNode node)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw new BundleCompilationException(
                ["recordScope must be a typed mapping; raw or nested predicate expressions are not supported."]);
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "field",
            "source",
            "linkedentity",
            "linkedfield"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode keyScalar)
            {
                throw new BundleCompilationException(
                    ["recordScope keys must be scalar field, source, linkedEntity, or linkedField names."]);
            }

            var key = NormalizeYamlKey(keyScalar.Value);
            if (!allowed.Contains(key))
            {
                throw new BundleCompilationException(
                    [$"recordScope contains unsupported member '{keyScalar.Value}'; raw or nested predicates are not supported."]);
            }

            if (!seen.Add(key))
            {
                throw new BundleCompilationException(
                    [$"recordScope contains duplicate member '{keyScalar.Value}'."]);
            }

            if (valueNode is not YamlScalarNode)
            {
                throw new BundleCompilationException(
                    [$"recordScope member '{keyScalar.Value}' must be a scalar identifier; nested predicates are not supported."]);
            }
        }
    }

    private static string NormalizeYamlKey(string? value) =>
        string.Concat((value ?? string.Empty)
            .Where(character => character is not '_' and not '-'))
            .ToLowerInvariant();

    private async Task<BundleAcceptanceTestDocument> LoadStrictAcceptanceTestFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new BundleCompilationException([$"{DisplayPath(path)} is empty."]);
        }

        try
        {
            return strictAcceptanceDeserializer.Deserialize<BundleAcceptanceTestDocument>(yaml);
        }
        catch (Exception ex)
        {
            throw new BundleCompilationException(
                [$"{DisplayPath(path)} could not be parsed as a strict acceptance contract: {ex.Message}"]);
        }
    }

    private static IEnumerable<string> EnumerateYamlFiles(string root, string folder)
    {
        var directory = Path.Combine(root, folder);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var fullPath = Path.GetFullPath(file);
            if (!IsInsideDirectory(root, fullPath))
            {
                throw new BundleCompilationException([$"{DisplayPath(fullPath)} is outside bundle directory."]);
            }

            yield return fullPath;
        }
    }

    private static bool IsInsideDirectory(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private static ErpModule? ResolveModule(
        string moduleId,
        IReadOnlyDictionary<string, ErpModule> modules,
        string file,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            errors.Add($"{DisplayPath(file)} must declare module.");
            return null;
        }

        if (modules.TryGetValue(moduleId, out var module))
        {
            return module;
        }

        errors.Add($"{DisplayPath(file)} references unknown module '{moduleId}'.");
        return null;
    }

    private static void AddUnique(
        string key,
        string source,
        IDictionary<string, string> sources,
        string kind,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            errors.Add($"{source} has a {kind} without a key.");
            return;
        }

        if (sources.TryGetValue(key, out var existing))
        {
            errors.Add($"{source} duplicates {kind} '{key}' already defined in {existing}.");
            return;
        }

        sources[key] = source;
    }

    private static void ValidateFolderReferences(
        ErpBundle bundle,
        IReadOnlyDictionary<string, string> entitySources,
        IReadOnlyDictionary<string, string> workflowSources,
        IReadOnlyDictionary<string, string> viewSources,
        IReadOnlyDictionary<string, string> actionSources,
        IReadOnlyDictionary<string, string> scheduleSources,
        IReadOnlyDictionary<string, string> reportSources,
        IReadOnlyDictionary<string, string> roleSources,
        List<string> errors)
    {
        foreach (var module in bundle.Modules)
        {
            foreach (var entity in module.Entities)
            {
                var source = entitySources.GetValueOrDefault(entity.Id) ?? $"entity '{entity.Id}'";
                foreach (var field in entity.Fields.Where(field => !string.IsNullOrWhiteSpace(field.References)))
                {
                    if (!entitySources.ContainsKey(field.References!))
                    {
                        errors.Add($"{source}: field `{field.Id}` references unknown entity `{field.References}`.");
                    }
                }
            }

            foreach (var view in module.Views)
            {
                var source = viewSources.GetValueOrDefault(view.Id) ?? $"view '{view.Id}'";
                if (!string.IsNullOrWhiteSpace(view.Entity) && !entitySources.ContainsKey(view.Entity))
                {
                    errors.Add($"{source}: view references unknown entity `{view.Entity}`.");
                }
            }

            foreach (var workflow in module.Workflows)
            {
                var source = workflowSources.GetValueOrDefault(workflow.Id) ?? $"workflow '{workflow.Id}'";
                if (!string.IsNullOrWhiteSpace(workflow.Entity) && !entitySources.ContainsKey(workflow.Entity))
                {
                    errors.Add($"{source}: workflow references unknown entity `{workflow.Entity}`.");
                }
            }
        }

        foreach (var action in bundle.Actions)
        {
            var source = actionSources.GetValueOrDefault(action.Key) ?? $"action '{action.Key}'";
            if (!string.IsNullOrWhiteSpace(action.Entity) && !entitySources.ContainsKey(action.Entity))
            {
                errors.Add($"{source}: action references unknown entity `{action.Entity}`.");
            }

            foreach (var permission in action.Requires.Permissions)
            {
                if (!string.IsNullOrWhiteSpace(permission.Entity) && !entitySources.ContainsKey(permission.Entity))
                {
                    errors.Add($"{source}: action permission references unknown entity `{permission.Entity}`.");
                }
            }

            foreach (var step in action.Steps.Where(step => !string.IsNullOrWhiteSpace(step.Entity)))
            {
                if (!entitySources.ContainsKey(step.Entity))
                {
                    errors.Add($"{source}: action step `{step.Id}` references unknown entity `{step.Entity}`.");
                }
            }
        }

        foreach (var schedule in bundle.Schedules)
        {
            var source = scheduleSources.GetValueOrDefault(schedule.Id) ?? $"schedule '{schedule.Id}'";
            if (!string.IsNullOrWhiteSpace(schedule.Entity) && !entitySources.ContainsKey(schedule.Entity))
            {
                errors.Add($"{source}: schedule references unknown entity `{schedule.Entity}`.");
            }

            if (!string.IsNullOrWhiteSpace(schedule.Workflow) && !workflowSources.ContainsKey(schedule.Workflow))
            {
                errors.Add($"{source}: schedule references unknown workflow `{schedule.Workflow}`.");
            }
        }

        foreach (var report in bundle.Reports)
        {
            var source = reportSources.GetValueOrDefault(report.Key) ?? $"report '{report.Key}'";
            if (!string.IsNullOrWhiteSpace(report.Source) && !entitySources.ContainsKey(report.Source))
            {
                errors.Add($"{source}: report references unknown source `{report.Source}`.");
            }
        }

        var reportKeys = new HashSet<string>(reportSources.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var widget in bundle.Dashboard.Widgets)
        {
            if (!string.IsNullOrWhiteSpace(widget.Report) && !reportKeys.Contains(widget.Report))
            {
                errors.Add($"dashboards: widget `{widget.Key}` references unknown report `{widget.Report}`.");
            }
        }

        foreach (var (roleId, role) in bundle.Roles)
        {
            var source = roleSources.GetValueOrDefault(roleId) ?? $"role '{roleId}'";
            foreach (var grant in role.Grants)
            {
                var scope = string.IsNullOrWhiteSpace(grant.Scope)
                    ? PermissionGrantScopes.Entity
                    : grant.Scope.Trim();
                var target = grant.Target.Trim().ToLowerInvariant();
                var usesEntityResource = !scope.Equals(PermissionGrantScopes.Page, StringComparison.OrdinalIgnoreCase) ||
                    target is "entity_list" or "entity_create" or "entity_detail";
                if (usesEntityResource &&
                    grant.Entity != "*" &&
                    !string.IsNullOrWhiteSpace(grant.Entity) &&
                    !entitySources.ContainsKey(grant.Entity))
                {
                    errors.Add($"{source}: role grant references unknown entity `{grant.Entity}`.");
                }
            }
        }
    }

    private static string DisplayPath(string path) =>
        path.Replace('\\', '/');

    private sealed class BundleManifestFile
    {
        public BundleManifest Bundle { get; init; } = new();

        public List<BundleManifestModule> Modules { get; init; } = [];
    }

    private sealed class BundleManifest
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;
    }

    private sealed class BundleManifestModule
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;
    }

    private sealed class BundleEntityFile
    {
        public string Module { get; init; } = string.Empty;

        public ErpEntity Entity { get; init; } = new();
    }

    private sealed class BundleViewFile
    {
        public string Module { get; init; } = string.Empty;

        public ErpView View { get; init; } = new();
    }

    private sealed class BundleWorkflowFile
    {
        public string Module { get; init; } = string.Empty;

        public ErpWorkflow Workflow { get; init; } = new();
    }

    private sealed class BundleActionFile
    {
        public ErpAction Action { get; init; } = new();
    }

    private sealed class BundleScheduleFile
    {
        public ErpSchedule Schedule { get; init; } = new();
    }

    private sealed class BundleReportFile
    {
        public ErpReport Report { get; init; } = new();
    }

    private sealed class BundleDashboardFile
    {
        public ErpDashboard Dashboard { get; init; } = new();
    }

    private sealed class BundleRolesFile
    {
        public Dictionary<string, ErpRole> Roles { get; init; } = [];
    }
}
