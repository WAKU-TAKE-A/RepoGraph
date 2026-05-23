using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Probe.Services.Analysis
{
    internal static class SerializationConventionExtractor
    {
        public static IReadOnlyList<FrameworkEntrypoint> GetEntrypoints(IMethodSymbol method, SyntaxNode declarationNode)
        {
            var entrypoints = new List<FrameworkEntrypoint>();

            foreach (var callbackName in GetSerializationCallbackAttributeNames(method, declarationNode))
            {
                entrypoints.Add(new FrameworkEntrypoint(
                    FrameworkRuleCatalog.SerializationAttributeCallback,
                    $"framework::serialization.attribute.{callbackName}",
                    callbackName,
                    "Framework.Serialization",
                    "SerializerCallback"));
            }

            if (LooksLikeNewtonsoftJsonConverterCallback(method))
            {
                entrypoints.Add(new FrameworkEntrypoint(
                    FrameworkRuleCatalog.SerializationJsonConverterCallback,
                    $"framework::serialization.json_converter.{method.Name}",
                    method.Name,
                    "Framework.Serialization",
                    "NewtonsoftJsonConverter"));
            }

            if (LooksLikeContractResolverCallback(method))
            {
                entrypoints.Add(new FrameworkEntrypoint(
                    FrameworkRuleCatalog.SerializationContractResolverCallback,
                    $"framework::serialization.contract_resolver.{method.Name}",
                    method.Name,
                    "Framework.Serialization",
                    "ContractResolver"));
            }

            return entrypoints;
        }

        private static bool LooksLikeNewtonsoftJsonConverterCallback(IMethodSymbol method)
        {
            if (method.Name is not ("ReadJson" or "WriteJson" or "CanConvert" or "Read" or "Write" or "ReadAsPropertyName" or "WriteAsPropertyName"))
            {
                return false;
            }

            if (IsOrDerivedFrom(method.ContainingType, "Newtonsoft.Json.JsonConverter") ||
                IsOrDerivedFrom(method.ContainingType, "Newtonsoft.Json.JsonConverter<T>") ||
                IsOrDerivedFrom(method.ContainingType, "System.Text.Json.Serialization.JsonConverter") ||
                IsOrDerivedFrom(method.ContainingType, "System.Text.Json.Serialization.JsonConverter<T>"))
            {
                return true;
            }

            var containingTypeName = method.ContainingType?.Name ?? "";
            if (!containingTypeName.EndsWith("Converter", StringComparison.Ordinal))
            {
                return false;
            }

            var parameterTypes = method.Parameters
                .Select(parameter => parameter.Type.OriginalDefinition.ToDisplayString())
                .ToArray();

            return method.Name switch
            {
                "ReadJson" => parameterTypes.Any(type => type.EndsWith("JsonReader", StringComparison.Ordinal)),
                "WriteJson" => parameterTypes.Any(type => type.EndsWith("JsonWriter", StringComparison.Ordinal)),
                "CanConvert" => parameterTypes.Any(type => string.Equals(type, "System.Type", StringComparison.Ordinal)),
                "Read" => parameterTypes.Any(type => type.EndsWith("Utf8JsonReader", StringComparison.Ordinal)),
                "Write" => parameterTypes.Any(type => type.EndsWith("Utf8JsonWriter", StringComparison.Ordinal)),
                "ReadAsPropertyName" => parameterTypes.Any(type => type.EndsWith("Utf8JsonReader", StringComparison.Ordinal)),
                "WriteAsPropertyName" => parameterTypes.Any(type => type.EndsWith("Utf8JsonWriter", StringComparison.Ordinal)),
                _ => false
            };
        }

        private static bool LooksLikeContractResolverCallback(IMethodSymbol method)
        {
            if (method.Name is not ("CreateProperty" or "CreateContract" or "CreateDictionaryContract"))
            {
                return false;
            }

            if (IsOrDerivedFrom(method.ContainingType, "Newtonsoft.Json.Serialization.DefaultContractResolver"))
            {
                return true;
            }

            var containingTypeName = method.ContainingType?.Name ?? "";
            return containingTypeName.EndsWith("ContractResolver", StringComparison.Ordinal);
        }

        private static IEnumerable<string> GetSerializationCallbackAttributeNames(IMethodSymbol method, SyntaxNode declarationNode)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in method.GetAttributes())
            {
                var attributeName = attribute.AttributeClass?.Name ?? "";
                if (TryNormalizeSerializationCallbackAttributeName(attributeName, out var callbackName))
                {
                    names.Add(callbackName);
                }
            }

            if (declarationNode is MemberDeclarationSyntax memberDeclaration)
            {
                foreach (var attributeList in memberDeclaration.AttributeLists)
                {
                    foreach (var attribute in attributeList.Attributes)
                    {
                        var attributeName = attribute.Name.ToString();
                        if (TryNormalizeSerializationCallbackAttributeName(attributeName, out var callbackName))
                        {
                            names.Add(callbackName);
                        }
                    }
                }
            }

            return names;
        }

        private static bool TryNormalizeSerializationCallbackAttributeName(string attributeName, out string callbackName)
        {
            callbackName = string.Empty;
            if (string.IsNullOrWhiteSpace(attributeName))
            {
                return false;
            }

            var simpleName = attributeName.Split('.').Last();
            if (simpleName is "OnDeserializedAttribute" or "OnDeserializingAttribute" or "OnSerializedAttribute" or "OnSerializingAttribute")
            {
                callbackName = simpleName.Replace("Attribute", string.Empty, StringComparison.Ordinal);
                return true;
            }

            if (simpleName is "OnDeserialized" or "OnDeserializing" or "OnSerialized" or "OnSerializing")
            {
                callbackName = simpleName;
                return true;
            }

            return false;
        }

        private static bool IsOrDerivedFrom(ITypeSymbol? type, string baseTypeFqn)
        {
            var current = type;
            while (current != null)
            {
                if (string.Equals(current.ToDisplayString(), baseTypeFqn, StringComparison.Ordinal))
                {
                    return true;
                }

                current = current.BaseType;
            }

            return false;
        }
    }
}
