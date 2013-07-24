﻿namespace Microsoft.VisualStudio.Composition
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.ComponentModel.Composition;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;
    using Validation;

    public class AttributedPartDiscoveryV1 : PartDiscovery
    {
        public override ComposablePartDefinition CreatePart(Type partType)
        {
            Requires.NotNull(partType, "partType");

            var exportsOnType = ImmutableList.CreateBuilder<ExportDefinition>();
            var exportsOnMembers = ImmutableDictionary.CreateBuilder<MemberInfo, ExportDefinition>();
            var imports = ImmutableDictionary.CreateBuilder<MemberInfo, ImportDefinition>();
            var exportMetadataOnType = GetExportMetadata(partType.GetCustomAttributes());
            var partCreationPolicy = CreationPolicy.Any;

            foreach (var exportAttribute in partType.GetCustomAttributes<ExportAttribute>())
            {
                var partTypeAsGenericTypeDefinition = partType.IsGenericType ? partType.GetGenericTypeDefinition() : null;
                var contract = new CompositionContract(exportAttribute.ContractName, exportAttribute.ContractType ?? partTypeAsGenericTypeDefinition ?? partType);
                var exportDefinition = new ExportDefinition(contract, exportMetadataOnType);
                exportsOnType.Add(exportDefinition);
            }

            var partCreationPolicyAttribute = partType.GetCustomAttribute<PartCreationPolicyAttribute>();
            string sharingBoundary = string.Empty;
            if (partCreationPolicyAttribute != null)
            {
                partCreationPolicy = partCreationPolicyAttribute.CreationPolicy;
                if (partCreationPolicyAttribute.CreationPolicy == CreationPolicy.NonShared)
                {
                    sharingBoundary = null;
                }
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var member in Enumerable.Concat<MemberInfo>(partType.GetProperties(flags), partType.GetFields(flags)))
            {
                var property = member as PropertyInfo;
                var field = member as FieldInfo;
                var propertyOrFieldType = property != null ? property.PropertyType : field.FieldType;
                var importAttribute = member.GetCustomAttribute<ImportAttribute>();
                var importManyAttribute = member.GetCustomAttribute<ImportManyAttribute>();
                var exportAttribute = member.GetCustomAttribute<ExportAttribute>();
                Requires.Argument(!(importAttribute != null && importManyAttribute != null), "partType", "Member \"{0}\" contains both ImportAttribute and ImportManyAttribute.", member.Name);
                Requires.Argument(!(exportAttribute != null && (importAttribute != null || importManyAttribute != null)), "partType", "Member \"{0}\" contains both import and export attributes.", member.Name);

                ImportDefinition importDefinition;
                if (TryCreateImportDefinition(propertyOrFieldType, member.GetCustomAttributes(), out importDefinition))
                {
                    imports.Add(member, importDefinition);
                }
                else if (exportAttribute != null)
                {
                    var exportMetadataOnMember = GetExportMetadata(member.GetCustomAttributes());
                    var contract = new CompositionContract(exportAttribute.ContractName, exportAttribute.ContractType ?? propertyOrFieldType);
                    var exportDefinition = new ExportDefinition(contract, exportMetadataOnMember);
                    exportsOnMembers.Add(member, exportDefinition);
                }
            }

            foreach (var method in partType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var exportAttribute = method.GetCustomAttribute<ExportAttribute>();
                if (exportAttribute != null)
                {
                    var exportMetadataOnMember = GetExportMetadata(method.GetCustomAttributes());
                    Type contractType = exportAttribute.ContractType ?? GetContractTypeForDelegate(method);
                    var contract = new CompositionContract(exportAttribute.ContractName, contractType);
                    var exportDefinition = new ExportDefinition(contract, exportMetadataOnMember);
                    exportsOnMembers.Add(method, exportDefinition);
                }
            }

            MethodInfo onImportsSatisfied = null;
            if (typeof(IPartImportsSatisfiedNotification).IsAssignableFrom(partType))
            {
                onImportsSatisfied = typeof(IPartImportsSatisfiedNotification).GetMethod("OnImportsSatisfied", BindingFlags.Public | BindingFlags.Instance);
            }

            if (exportsOnMembers.Count > 0 || exportsOnType.Count > 0)
            {
                var importingConstructorParameters = ImmutableList.CreateBuilder<ImportDefinition>();
                var importingCtor = GetImportingConstructor(partType, typeof(ImportingConstructorAttribute), publicOnly: false);
                foreach (var parameter in importingCtor.GetParameters())
                {
                    var importDefinition = CreateImportDefinition(parameter.ParameterType, parameter.GetCustomAttributes());
                    if (importDefinition.Cardinality == ImportCardinality.ZeroOrMore)
                    {
                        Verify.Operation(PartDiscovery.IsImportManyCollectionTypeCreateable(importDefinition), "Collection must be public with a public constructor when used with an [ImportingConstructor].");
                    }

                    importingConstructorParameters.Add(importDefinition);
                }

                return new ComposablePartDefinition(partType, exportsOnType.ToImmutable(), exportsOnMembers.ToImmutable(), imports.ToImmutable(), sharingBoundary, onImportsSatisfied, importingConstructorParameters.ToImmutable(), partCreationPolicy);
            }
            else
            {
                return null;
            }
        }

        private static bool TryCreateImportDefinition(Type propertyOrFieldType, IEnumerable<Attribute> attributes, out ImportDefinition importDefinition)
        {
            Requires.NotNull(propertyOrFieldType, "propertyOrFieldType");

            var importAttribute = attributes.OfType<ImportAttribute>().SingleOrDefault();
            var importManyAttribute = attributes.OfType<ImportManyAttribute>().SingleOrDefault();

            if (importAttribute != null)
            {
                var requiredCreationPolicy = propertyOrFieldType.IsExportFactoryTypeV1()
                    ? CreationPolicy.NonShared
                    : importAttribute.RequiredCreationPolicy;

                Type contractType = importAttribute.ContractType ?? GetElementFromImportingMemberType(propertyOrFieldType, importMany: false);
                var contract = new CompositionContract(importAttribute.ContractName, contractType);
                importDefinition = new ImportDefinition(
                    contract,
                    importAttribute.AllowDefault ? ImportCardinality.OneOrZero : ImportCardinality.ExactlyOne,
                    propertyOrFieldType,
                    ImmutableList.Create<IImportSatisfiabilityConstraint>(),
                    requiredCreationPolicy);
                return true;
            }
            else if (importManyAttribute != null)
            {
                var requiredCreationPolicy = GetElementTypeFromMany(propertyOrFieldType).IsExportFactoryTypeV1()
                    ? CreationPolicy.NonShared
                    : importManyAttribute.RequiredCreationPolicy;

                Type contractType = importManyAttribute.ContractType ?? GetElementFromImportingMemberType(propertyOrFieldType, importMany: true);
                var contract = new CompositionContract(importManyAttribute.ContractName, contractType);
                importDefinition = new ImportDefinition(
                    contract,
                    ImportCardinality.ZeroOrMore,
                    propertyOrFieldType,
                    ImmutableList.Create<IImportSatisfiabilityConstraint>(),
                    requiredCreationPolicy);
                return true;
            }
            else
            {
                importDefinition = null;
                return false;
            }
        }

        private static ImportDefinition CreateImportDefinition(Type propertyOrFieldType, IEnumerable<Attribute> attributes)
        {
            ImportDefinition result;
            if (!TryCreateImportDefinition(propertyOrFieldType, attributes, out result))
            {
                Assumes.True(TryCreateImportDefinition(propertyOrFieldType, attributes.Concat(new Attribute[] { new ImportAttribute() }), out result));
            }

            return result;
        }

        private static IReadOnlyDictionary<string, object> GetExportMetadata(IEnumerable<Attribute> attributes)
        {
            Requires.NotNull(attributes, "attributes");

            var result = ImmutableDictionary.CreateBuilder<string, object>();
            foreach (var attribute in attributes)
            {
                var exportMetadataAttribute = attribute as ExportMetadataAttribute;
                if (exportMetadataAttribute != null)
                {
                    result.Add(exportMetadataAttribute.Name, exportMetadataAttribute.Value);
                }
                else if (attribute.GetType().GetCustomAttribute<MetadataAttributeAttribute>() != null)
                {
                    var properties = attribute.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var property in properties.Where(p => p.DeclaringType != typeof(Attribute)))
                    {
                        result.Add(property.Name, property.GetValue(attribute));
                    }
                }
            }

            return result.ToImmutable();
        }

        private static Type GetContractTypeForDelegate(MethodInfo method)
        {
            Type genericTypeDefinition;
            int parametersCount = method.GetParameters().Length;
            var typeArguments = method.GetParameters().Select(p => p.ParameterType).ToList();
            var voidResult = method.ReturnType.IsEquivalentTo(typeof(void));
            if (voidResult)
            {
                if (typeArguments.Count == 0)
                {
                    return typeof(Action);
                }

                genericTypeDefinition = Type.GetType("System.Action`" + typeArguments.Count);
            }
            else
            {
                typeArguments.Add(method.ReturnType);
                genericTypeDefinition = Type.GetType("System.Func`" + typeArguments.Count);
            }

            return genericTypeDefinition.MakeGenericType(typeArguments.ToArray());
        }

        public override IReadOnlyCollection<ComposablePartDefinition> CreateParts(Assembly assembly)
        {
            Requires.NotNull(assembly, "assembly");

            var parts = from type in assembly.GetTypes()
                        where type.GetCustomAttribute<PartNotDiscoverableAttribute>() == null
                        let part = this.CreatePart(type)
                        where part != null
                        select part;
            return parts.ToImmutableArray();
        }
    }
}
