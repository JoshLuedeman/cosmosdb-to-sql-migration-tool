using System.Text.Json.Nodes;

namespace CosmosToSqlAssessment.Services.DataFactory;

/// <summary>
/// Builds the deployable Azure Resource Manager template that wraps every emitted
/// ADF artifact (linked services, datasets, pipelines) under
/// <c>Microsoft.DataFactory/factories/{kind}</c> resources (#146).
///
/// Design notes:
/// <list type="bullet">
///   <item><b>Serialise-then-parse input</b>: every per-artifact <c>properties</c> bag
///   is captured as a <see cref="JsonObject"/> (via <c>JsonSerializer.SerializeToNode</c>)
///   so <c>[JsonExtensionData] AdditionalProperties</c> is already flattened — the ARM
///   template payload matches what's written to the per-artifact JSON file byte-for-byte
///   (modulo the targeted defaultValue rewrite below).</item>
///   <item><b>Generic reference walker</b>: <see cref="CollectReferences"/> recurses
///   through every <see cref="JsonObject"/> /<see cref="JsonArray"/> in the properties
///   bag looking for <c>{ "type": "*Reference", "referenceName": &lt;name&gt; }</c> triples.
///   This automatically catches Dataset / LinkedService / Pipeline references plus the
///   Key Vault <c>LinkedServiceReference</c> nested under <c>typeProperties.accountKey.store</c>
///   on the Cosmos/SQL linked services — no per-activity-type code needed.</item>
///   <item><b>Closed-world dependsOn</b>: edges are only emitted for resources actually
///   present in this template's resource set. External / operator-managed linked services
///   (e.g. a fault-tolerance log storage LS the operator has pre-provisioned) are silently
///   skipped here — the operator's existing deployment of those resources satisfies them
///   at <c>New-AzResourceGroupDeployment</c> time.</item>
///   <item><b>Targeted defaultValue rewrite</b>: when a pipeline's
///   <see cref="ArmResourceInput.PipelineParameterArmOverrides"/> map is non-null, the
///   builder walks <c>properties.parameters.&lt;pipelineParamName&gt;.defaultValue</c> and
///   replaces the literal with <c>[parameters('&lt;armParamName&gt;')]</c>. This is the
///   only mechanism that actually flows deployment-time parameter values into deployed
///   ADF pipelines — without it, the parameter file would be decorative.</item>
///   <item><b>Determinism</b>: resources sorted by (kind, name) and dependsOn entries
///   sorted alphabetically so the file diff is stable across runs.</item>
/// </list>
/// </summary>
public sealed class ArmTemplateBuilder
{
    public const string ApiVersion = "2018-06-01";
    public const string FactoriesResourceType = "Microsoft.DataFactory/factories";

    public const string LinkedServiceKind = "linkedservices";
    public const string DatasetKind = "datasets";
    public const string PipelineKind = "pipelines";

    private const string DeploymentSchema =
        "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#";

    private static readonly IReadOnlyDictionary<string, string> ReferenceTypeToKind =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LinkedServiceReference"] = LinkedServiceKind,
            ["DatasetReference"] = DatasetKind,
            ["PipelineReference"] = PipelineKind,
        };

    /// <summary>
    /// Builds the ARM template root object.
    /// </summary>
    /// <param name="resources">All ADF artifacts to wrap. Order does not matter — the
    /// builder sorts them by <c>(Kind, Name)</c> on the way out.</param>
    /// <param name="parameters">ARM-template-level parameter declarations. Must include
    /// the factory-name parameter (caller's responsibility) plus every parameter the
    /// operator can override at deployment time.</param>
    /// <param name="factoryParameterName">Name of the ARM parameter that holds the
    /// target data factory's resource name (usually <c>"dataFactoryName"</c>). Used in
    /// every resource's <c>name</c> and <c>dependsOn</c> expression.</param>
    public Dictionary<string, object?> Build(
        IReadOnlyList<ArmResourceInput> resources,
        IReadOnlyDictionary<string, ArmParameterDefinition> parameters,
        string factoryParameterName)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryParameterName);

        // Closed-world set of (kind, name) tuples — used to filter dependsOn edges so we
        // never reference a sibling resource we aren't actually emitting.
        var emitted = new HashSet<(string Kind, string Name)>(
            resources.Select(r => (r.Kind, r.Name)));

        var armResources = new List<Dictionary<string, object?>>(resources.Count);
        foreach (var input in resources
                     .OrderBy(r => KindOrder(r.Kind), Comparer<int>.Default)
                     .ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            armResources.Add(BuildResource(input, emitted, factoryParameterName));
        }

        var armParameters = new Dictionary<string, object?>(parameters.Count, StringComparer.Ordinal);
        foreach (var kv in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            armParameters[kv.Key] = BuildParameterShape(kv.Value);
        }

        return new Dictionary<string, object?>
        {
            ["$schema"] = DeploymentSchema,
            ["contentVersion"] = "1.0.0.0",
            ["parameters"] = armParameters,
            ["resources"] = armResources,
        };
    }

    private static Dictionary<string, object?> BuildResource(
        ArmResourceInput input,
        HashSet<(string Kind, string Name)> emitted,
        string factoryParameterName)
    {
        // Deep clone so per-pipeline defaultValue rewrites don't leak back into the
        // caller's input (which mirrors what was written to the per-artifact JSON file).
        var properties = (JsonObject)input.Properties.DeepClone();

        if (input.PipelineParameterArmOverrides is { Count: > 0 } overrides)
        {
            RewritePipelineParameterDefaults(properties, overrides);
        }

        var refs = new List<(string Kind, string Name)>();
        CollectReferences(properties, refs);

        var dependsOn = refs
            .Where(r => emitted.Contains(r))
            .Where(r => !(r.Kind == input.Kind &&
                          string.Equals(r.Name, input.Name, StringComparison.Ordinal))) // skip self
            .Distinct()
            .OrderBy(r => r.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (object?)BuildResourceIdExpression(r.Kind, r.Name, factoryParameterName))
            .ToList();

        var resource = new Dictionary<string, object?>
        {
            ["type"] = $"{FactoriesResourceType}/{input.Kind}",
            ["apiVersion"] = ApiVersion,
            ["name"] = $"[concat(parameters('{factoryParameterName}'), '/{input.Name}')]",
        };
        if (dependsOn.Count > 0)
        {
            resource["dependsOn"] = dependsOn;
        }
        // System.Text.Json serialises JsonObject as a sub-document — this round-trips through
        // AdfJsonSerializer cleanly because JsonObject is JSON-native.
        resource["properties"] = properties;
        return resource;
    }

    /// <summary>
    /// Walks <c>properties.parameters.&lt;pipelineParamName&gt;.defaultValue</c> and
    /// rewrites the literal to <c>"[parameters('&lt;armParamName&gt;')]"</c>. Silently
    /// ignores entries with no <c>defaultValue</c> (e.g. linked-service parameter blocks
    /// which only have <c>type</c>) and pipeline params not in the override map.
    /// </summary>
    private static void RewritePipelineParameterDefaults(
        JsonObject properties,
        IReadOnlyDictionary<string, string> overrides)
    {
        if (properties["parameters"] is not JsonObject paramBlock)
        {
            return;
        }

        foreach (var (pipelineParamName, armParamName) in overrides)
        {
            if (paramBlock[pipelineParamName] is not JsonObject paramObj)
            {
                continue;
            }
            // Only rewrite if the operator gave us a real ARM parameter to bind to,
            // and only if the param has a defaultValue to overwrite (no defaultValue =
            // operator must always supply explicitly; skip).
            if (!paramObj.ContainsKey("defaultValue"))
            {
                continue;
            }
            paramObj["defaultValue"] = $"[parameters('{armParamName}')]";
        }
    }

    /// <summary>
    /// Recursively walks the JSON tree looking for any object of shape
    /// <c>{ "type": "&lt;X&gt;Reference", "referenceName": &lt;name&gt; }</c>. Records the
    /// pair as <c>(kindFromType, referenceName)</c> in <paramref name="acc"/>.
    /// </summary>
    private static void CollectReferences(JsonNode? node, List<(string Kind, string Name)> acc)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.TryGetPropertyValue("type", out var typeNode)
                    && typeNode is JsonValue typeValue
                    && typeValue.TryGetValue(out string? typeStr)
                    && typeStr is not null
                    && ReferenceTypeToKind.TryGetValue(typeStr, out var kind)
                    && obj.TryGetPropertyValue("referenceName", out var nameNode)
                    && nameNode is JsonValue nameValue
                    && nameValue.TryGetValue(out string? nameStr)
                    && !string.IsNullOrWhiteSpace(nameStr))
                {
                    acc.Add((kind, nameStr));
                }
                foreach (var kv in obj)
                {
                    CollectReferences(kv.Value, acc);
                }
                break;
            }
            case JsonArray arr:
                foreach (var item in arr)
                {
                    CollectReferences(item, acc);
                }
                break;
        }
    }

    private static string BuildResourceIdExpression(string kind, string artifactName, string factoryParameterName) =>
        $"[resourceId('{FactoriesResourceType}/{kind}', parameters('{factoryParameterName}'), '{artifactName}')]";

    /// <summary>
    /// Stable kind ordering: linked services emitted first (they're dependency roots),
    /// then datasets (depend on LS), then pipelines (depend on both). Within a kind we
    /// sort alphabetically by name (also handled by the caller, but doing both is harmless).
    /// </summary>
    private static int KindOrder(string kind) => kind switch
    {
        LinkedServiceKind => 0,
        DatasetKind => 1,
        PipelineKind => 2,
        _ => 99,
    };

    private static Dictionary<string, object?> BuildParameterShape(ArmParameterDefinition definition)
    {
        var shape = new Dictionary<string, object?>
        {
            ["type"] = string.IsNullOrWhiteSpace(definition.Type) ? "string" : definition.Type,
        };
        if (definition.HasDefaultValue)
        {
            shape["defaultValue"] = definition.DefaultValue;
        }
        if (!string.IsNullOrWhiteSpace(definition.Description))
        {
            shape["metadata"] = new Dictionary<string, object?>
            {
                ["description"] = definition.Description,
            };
        }
        return shape;
    }
}

/// <summary>
/// One ADF artifact slated for inclusion in the ARM template.
/// </summary>
/// <param name="Kind">ARM resource-kind segment: <c>linkedservices</c>, <c>datasets</c>,
/// or <c>pipelines</c>. Casing matches Azure's documented resource type.</param>
/// <param name="Name">Logical artifact name (no factory prefix). Used in <c>name</c>
/// and <c>resourceId</c> expressions.</param>
/// <param name="Properties">The <c>properties</c> JSON bag captured AFTER serializing
/// the artifact (so <c>[JsonExtensionData]</c> is already flattened). The builder
/// deep-clones before mutating, so callers can reuse the same node across multiple
/// builds.</param>
/// <param name="PipelineParameterArmOverrides">When non-null, the builder rewrites
/// <c>properties.parameters.&lt;K&gt;.defaultValue</c> to
/// <c>"[parameters('&lt;V&gt;')]"</c> for every <c>K → V</c> entry. Only meaningful
/// for pipeline artifacts; pass <c>null</c> for linked services / datasets.</param>
public sealed record ArmResourceInput(
    string Kind,
    string Name,
    JsonObject Properties,
    IReadOnlyDictionary<string, string>? PipelineParameterArmOverrides = null);

/// <summary>
/// ARM-template parameter declaration. ARM uses
/// <c>{ "type": ..., "defaultValue": ..., "metadata": { "description": ... } }</c> shape
/// — distinct from the deployment-parameter file's <c>{ "value": ..., "metadata": ... }</c>
/// shape (which lives in <c>adf-parameters.template.json</c>).
/// </summary>
/// <param name="Type">ARM type: <c>"string"</c>, <c>"int"</c>, <c>"bool"</c>, etc.</param>
/// <param name="DefaultValue">Default. When <see cref="HasDefaultValue"/> is <c>false</c>
/// (use <see cref="Required"/>) the operator must supply a value at deployment time.</param>
/// <param name="Description">Optional metadata.description string surfaced by
/// <c>az deployment group validate</c> and the Portal's parameter editor.</param>
public sealed record ArmParameterDefinition(
    string Type,
    object? DefaultValue,
    string? Description)
{
    /// <summary>
    /// <c>true</c> when <see cref="DefaultValue"/> is set (including empty string). When
    /// <c>false</c> the builder omits the <c>defaultValue</c> field so ARM enforces that
    /// the operator supplies the value.
    /// </summary>
    public bool HasDefaultValue { get; init; } = true;

    /// <summary>Returns a parameter definition with no default — operator must supply.</summary>
    public static ArmParameterDefinition Required(string type, string? description) =>
        new(type, DefaultValue: null, Description: description) { HasDefaultValue = false };

    /// <summary>Returns a string parameter with a literal default.</summary>
    public static ArmParameterDefinition String(string? defaultValue, string? description) =>
        new("string", DefaultValue: defaultValue, Description: description);
}
