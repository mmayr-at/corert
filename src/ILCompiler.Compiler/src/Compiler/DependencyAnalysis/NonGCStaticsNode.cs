// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Internal.Text;
using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler.DependencyAnalysis
{
    /// <summary>
    /// Represents a node with non-GC static data associated with a type, along
    /// with it's class constructor context. The non-GC static data region shall be prefixed
    /// with the class constructor context if the type has a class constructor that
    /// needs to be triggered before the type members can be accessed.
    /// </summary>
    public class NonGCStaticsNode : ObjectNode, IExportableSymbolNode
    {
        private MetadataType _type;
        private NodeFactory _factory;

        public NonGCStaticsNode(MetadataType type, NodeFactory factory)
        {
            Debug.Assert(!type.IsCanonicalSubtype(CanonicalFormKind.Specific));
            _type = type;
            _factory = factory;
        }

        protected override string GetName(NodeFactory factory) => this.GetMangledName(factory.NameMangler);

        public override ObjectNodeSection Section => ObjectNodeSection.DataSection;

        public static string GetMangledName(TypeDesc type, NameMangler nameMangler)
        {
            return "__NonGCStaticBase_" + nameMangler.GetMangledTypeName(type);
        }
 
        public void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append("__NonGCStaticBase_").Append(nameMangler.GetMangledTypeName(_type)); 
        }

        int ISymbolNode.Offset => 0;

        int ISymbolDefinitionNode.Offset
        {
            get
            {
                // Make sure the NonGCStatics symbol always points to the beginning of the data.
                if (_factory.TypeSystemContext.HasLazyStaticConstructor(_type))
                {
                    return GetClassConstructorContextStorageSize(_factory.Target, _type);
                }
                else
                {
                    return 0;
                }
            }
        }

        public override bool IsShareable => EETypeNode.IsTypeNodeShareable(_type);

        public MetadataType Type => _type;

        public virtual bool IsExported(NodeFactory factory)
        {
            return factory.CompilationModuleGroup.ExportsType(Type);
        }

        private static int GetClassConstructorContextSize(TargetDetails target)
        {
            // TODO: Assert that StaticClassConstructionContext type has the expected size
            //       (need to make it a well known type?)
            return target.PointerSize * 2;
        }

        public static int GetClassConstructorContextStorageSize(TargetDetails target, MetadataType type)
        {
            int alignmentRequired = Math.Max(type.NonGCStaticFieldAlignment.AsInt, GetClassConstructorContextAlignment(target));
            return AlignmentHelper.AlignUp(GetClassConstructorContextSize(type.Context.Target), alignmentRequired);
        }

        private static int GetClassConstructorContextAlignment(TargetDetails target)
        {
            // TODO: Assert that StaticClassConstructionContext type has the expected alignment
            //       (need to make it a well known type?)
            return target.PointerSize;
        }

        public override bool StaticDependenciesAreComputed => true;

        protected override DependencyList ComputeNonRelocationBasedDependencies(NodeFactory factory)
        {
            if (factory.TypeSystemContext.HasEagerStaticConstructor(_type))
            {
                var result = new DependencyList();
                result.Add(factory.EagerCctorIndirection(_type.GetStaticConstructor()), "Eager .cctor");
                return result;
            }

            return null;
        }

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly)
        {
            ObjectDataBuilder builder = new ObjectDataBuilder(factory, relocsOnly);

            // If the type has a class constructor, its non-GC statics section is prefixed  
            // by System.Runtime.CompilerServices.StaticClassConstructionContext struct.
            if (factory.TypeSystemContext.HasLazyStaticConstructor(_type))
            {
                int alignmentRequired = Math.Max(_type.NonGCStaticFieldAlignment.AsInt, GetClassConstructorContextAlignment(_type.Context.Target));
                int classConstructorContextStorageSize = GetClassConstructorContextStorageSize(factory.Target, _type);
                builder.RequireInitialAlignment(alignmentRequired);
                
                Debug.Assert(classConstructorContextStorageSize >= GetClassConstructorContextSize(_type.Context.Target));

                // Add padding before the context if alignment forces us to do so
                builder.EmitZeros(classConstructorContextStorageSize - GetClassConstructorContextSize(_type.Context.Target));

                // Emit the actual StaticClassConstructionContext
                MethodDesc cctorMethod = _type.GetStaticConstructor();
                MethodDesc canonCctorMethod = cctorMethod.GetCanonMethodTarget(CanonicalFormKind.Specific);
                if (cctorMethod != canonCctorMethod)
                    builder.EmitPointerReloc(factory.FatFunctionPointer(cctorMethod));
                else
                    builder.EmitPointerReloc(factory.MethodEntrypoint(cctorMethod));
                builder.EmitZeroPointer();
            }
            else
            {
                builder.RequireInitialAlignment(_type.NonGCStaticFieldAlignment.AsInt);
            }

            builder.EmitZeros(_type.NonGCStaticFieldSize.AsInt);
            builder.AddSymbol(this);

            return builder.ToObjectData();
        }
    }
}