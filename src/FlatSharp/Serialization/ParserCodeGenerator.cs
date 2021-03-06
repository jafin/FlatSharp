﻿/*
 * Copyright 2018 James Courtney
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace FlatSharp
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using FlatSharp.TypeModel;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;

    /// <summary>
    /// Generates a collection of methods to help parse the given root type.
    /// </summary>
    internal class ParserCodeGenerator
    {
        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        private readonly FlatBufferSerializerOptions options;

        private List<SyntaxNode> methodDeclarations = new List<SyntaxNode>();

        public ParserCodeGenerator(FlatBufferSerializerOptions options, IReadOnlyDictionary<Type, string> methodNames)
        {
            this.options = options;
            this.MethodNames = methodNames;
        }

        public IReadOnlyDictionary<Type, string> MethodNames { get; }

        public IEnumerable<SyntaxNode> MethodDeclarations => this.methodDeclarations;

        public void ImplementMethods()
        {
            foreach (var pair in this.MethodNames)
            {
                Type type = pair.Key;
                this.ImplementMethod(type);
            }
        }

        private void ImplementMethod(Type type)
        {
            var typeModel = RuntimeTypeModel.CreateFrom(type);

            if (typeModel is TableTypeModel tableModel)
            {
                this.ImplementTableReadMethod(tableModel);
            }
            else if (typeModel is StructTypeModel structModel)
            {
                this.ImplementStructReadMethod(structModel);
            }
            else if (typeModel is VectorTypeModel vectorModel)
            {
                if (vectorModel.IsMemoryVector)
                {
                    this.ImplementMemoryVectorReadMethod(vectorModel);
                }
                else if (vectorModel.IsArray)
                {
                    this.ImplementArrayVectorReadMethod(vectorModel);
                }
                else
                {
                    this.ImplementListVectorReadMethod(vectorModel);
                }
            }
            else if (typeModel is EnumTypeModel enumModel)
            {
                this.ImplementEnumReadMethod(enumModel);
            }
            else if (typeModel is UnionTypeModel)
            {
                // Explicitly left empty.
            }
            else
            {
                throw new InvalidOperationException();
            }
        }

        private void ImplementTableReadMethod(TableTypeModel typeModel)
        {
            // We have to implement two items: The table class and the overall "read" method.
            // Let's start with the read method.
            string className = "tableReader_" + Guid.NewGuid().ToString("n");
            this.GenerateMethodDefinition(typeModel.ClrType, $"return new {className}(memory, offset + memory.{nameof(InputBuffer.ReadUOffset)}(offset));");

            // Build up a list of property overrides.
            var propertyOverrides = new List<GeneratedProperty>();
            foreach (var item in typeModel.IndexToMemberMap)
            {
                int index = item.Key;
                var value = item.Value;

                GeneratedProperty propertyStuff;
                if (value.ItemTypeModel is UnionTypeModel)
                {
                    propertyStuff = this.CreateUnionTableGetter(value, index);
                }
                else
                {
                    propertyStuff = this.CreateStandardTableProperty(value, index);
                }

                propertyOverrides.Add(propertyStuff);
            }

            string classDefinition = this.CreateClass(
                className,
                typeModel.ClrType,
                typeModel.IndexToMemberMap.Values.Select(x => x.PropertyInfo.Name),
                propertyOverrides);

            var node = CSharpSyntaxTree.ParseText(classDefinition, ParseOptions);
            this.methodDeclarations.Add(node.GetRoot());
        }

        /// <summary>
        /// Generates a standard getter for a normal vtable entry.
        /// </summary>
        private GeneratedProperty CreateStandardTableProperty(TableMemberModel memberModel, int index)
        {
            Type propertyType = memberModel.ItemTypeModel.ClrType;
            string defaultValue = CSharpHelpers.GetDefaultValueToken(memberModel);
            GeneratedProperty property = new GeneratedProperty(this.options, index, memberModel.PropertyInfo);

            property.ReadValueMethodDefinition =
$@"
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    private static {CSharpHelpers.GetCompilableTypeName(propertyType)} {property.ReadValueMethodName}(InputBuffer buffer, int offset)
                    {{
                        int absoluteLocation = buffer.{nameof(InputBuffer.GetAbsoluteTableFieldLocation)}(offset, {index});
                        if (absoluteLocation == 0) {{
                            return {defaultValue};
                        }}
                        else {{
                            return {this.GetReadInvocation(propertyType, "buffer", "absoluteLocation")};
                        }}
                    }}
";

            return property;
        }

        /// <summary>
        /// Generates a special property getter for union types. This stems from
        /// the fact that unions occupy two spots in the table's vtable to deserialize one
        /// logical field. This means that the logic to read them must also be special.
        /// </summary>
        private GeneratedProperty CreateUnionTableGetter(TableMemberModel memberModel, int index)
        {
            Type propertyType = memberModel.ItemTypeModel.ClrType;
            string defaultValue = CSharpHelpers.GetDefaultValueToken(memberModel);
            UnionTypeModel unionModel = (UnionTypeModel)memberModel.ItemTypeModel;

            GeneratedProperty generatedProperty = new GeneratedProperty(this.options, index, memberModel.PropertyInfo);

            // Start by generating switch cases. The codegen'ed union types have
            // well-defined constructors for each constituent type, so this .ctor
            // will always be available.
            List<string> switchCases = new List<string>();
            for (int i = 0; i < unionModel.UnionElementTypeModel.Length; ++i)
            {
                var unionMember = unionModel.UnionElementTypeModel[i];
                int unionIndex = i + 1;
                string structOffsetAdjustment = string.Empty;
                if (unionMember.SchemaType == FlatBufferSchemaType.Struct)
                {
                    structOffsetAdjustment = $"offsetLocation += buffer.{nameof(InputBuffer.ReadUOffset)}(offsetLocation);";
                }

                string @case =
$@"
                    case {unionIndex}:
                        {structOffsetAdjustment}
                        return new {CSharpHelpers.GetCompilableTypeName(unionModel.ClrType)}({this.GetReadInvocation(unionMember.ClrType, "buffer", "offsetLocation")});
";
                switchCases.Add(@case);
            }


            generatedProperty.ReadValueMethodDefinition =
$@"
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    private static {CSharpHelpers.GetCompilableTypeName(propertyType)} {generatedProperty.ReadValueMethodName}(InputBuffer buffer, int offset)
                    {{
                        int discriminatorLocation = buffer.{nameof(InputBuffer.GetAbsoluteTableFieldLocation)}(offset, {index});
                        int offsetLocation = buffer.{nameof(InputBuffer.GetAbsoluteTableFieldLocation)}(offset, {index + 1});
                            
                        if (discriminatorLocation == 0) {{
                            return {defaultValue};
                        }}
                        else {{
                            byte discriminator = buffer.{nameof(InputBuffer.ReadByte)}(discriminatorLocation);
                            if (discriminator == 0 && offsetLocation != 0)
                                throw new System.IO.InvalidDataException(""FlatBuffer union had discriminator set but no offset."");
                            switch (discriminator)
                            {{
                                {string.Join("\r\n", switchCases)}
                                default:
                                    return {defaultValue};
                            }}
                        }}
                    }}
";
            return generatedProperty;
        }

        private void ImplementStructReadMethod(StructTypeModel typeModel)
        {
            // We have to implement two items: The table class and the overall "read" method.
            // Let's start with the read method.
            string className = "structReader_" + Guid.NewGuid().ToString("n");

            // Static factory method.
            this.GenerateMethodDefinition(typeModel.ClrType, $"return new {className}(memory, offset);");

            // Implement the class
            {
                // Build up a list of property overrides.
                var propertyOverrides = new List<GeneratedProperty>();
                for (int index = 0; index < typeModel.Members.Count; ++index)
                {
                    var value = typeModel.Members[index];
                    PropertyInfo propertyInfo = value.PropertyInfo;
                    Type propertyType = propertyInfo.PropertyType;
                    string compilableTypeName = CSharpHelpers.GetCompilableTypeName(propertyType);

                    GeneratedProperty generatedProperty = new GeneratedProperty(this.options, index, propertyInfo);
                    generatedProperty.ReadValueMethodDefinition =
$@"
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    private static {CSharpHelpers.GetCompilableTypeName(propertyType)} {generatedProperty.ReadValueMethodName}(InputBuffer buffer, int offset)
                    {{
                        return {this.GetReadInvocation(propertyType, "buffer", $"offset + {value.Offset}")};
                    }}
";

                    propertyOverrides.Add(generatedProperty);
                }

                string classDefinition = this.CreateClass(
                    className,
                    typeModel.ClrType,
                    typeModel.Members.Select(x => x.PropertyInfo.Name),
                    propertyOverrides);
                var node = CSharpSyntaxTree.ParseText(classDefinition, ParseOptions);
                this.methodDeclarations.Add(node.GetRoot());
            }
        }

        private string CreateClass(
            string className,
            Type baseType,
            IEnumerable<string> propertyNames,
            IEnumerable<GeneratedProperty> propertyOverrides)
        {
            string inputBufferFieldDef = "private readonly InputBuffer buffer;";
            string offsetFieldDef = "private readonly int offset;";

            string ctorBody =
$@"
                this.buffer = buffer;
                this.offset = offset;
";

            if (this.options.GreedyDeserialize)
            {
                inputBufferFieldDef = string.Empty;
                offsetFieldDef = string.Empty;
                ctorBody = string.Join("\r\n", propertyOverrides.Select(x => $"this.{x.BackingFieldName} = {x.ReadValueMethodName}(buffer, offset);"));
            }

            return
$@"
                private sealed class {className} : {CSharpHelpers.GetCompilableTypeName(baseType)}
                {{
                    {inputBufferFieldDef}
                    {offsetFieldDef}
        
                    public {className}(InputBuffer buffer, int offset)
                    {{
                        {ctorBody}
                    }}

                    {string.Join("\r\n", propertyOverrides)}
                }}
";
        }

        private void ImplementMemoryVectorReadMethod(VectorTypeModel typeModel)
        {
            string invocation;
            if (typeModel.ItemTypeModel.ClrType == typeof(byte))
            {
                invocation = nameof(InputBuffer.ReadByteMemoryBlock);
                if (typeModel.IsReadOnly)
                {
                    invocation = nameof(InputBuffer.ReadByteReadOnlyMemoryBlock);
                }
            }
            else
            {
                string methodName = nameof(InputBuffer.ReadMemoryBlock);
                if (typeModel.IsReadOnly)
                {
                    methodName = nameof(InputBuffer.ReadReadOnlyMemoryBlock);
                }

                invocation =  $"{methodName}<{CSharpHelpers.GetCompilableTypeName(typeModel.ItemTypeModel.ClrType)}>";
            }

            string body = $"memory.{invocation}(offset, {typeModel.ItemTypeModel.InlineSize})";

            // Greedy deserialize has the invariant that we no longer touch the
            // original buffer. This means a memory copy here.
            if (this.options.GreedyDeserialize)
            {
                body = $"{body}.ToArray().AsMemory()";
            }

            this.GenerateMethodDefinition(typeModel.ClrType, $"return {body};");
        }

        private void ImplementListVectorReadMethod(VectorTypeModel typeModel)
        {
            string body = this.CreateFlatBufferVector(typeModel);

            if (this.options.PreallocateVectors)
            {
                // We just call .ToList(). Noe that when full greedy mode is on, these items will be 
                // greedily initialized as we traverse the list. Otherwise, they'll be allocated lazily.
                body += $".{nameof(SerializationHelpers.FlatBufferVectorToList)}()";

                if (!this.options.GenerateMutableObjects)
                {
                    // Finally, if we're not in the business of making mutable objects, then convert the list to read only.
                    body += ".AsReadOnly()";
                }
            }

            this.GenerateMethodDefinition(typeModel.ClrType, $"return {body};");
        }

        private void ImplementArrayVectorReadMethod(VectorTypeModel typeModel)
        {
            var itemTypeModel = typeModel.ItemTypeModel;

            string statement;
            if (itemTypeModel is ScalarTypeModel scalarModel && scalarModel.NativelyReadableFromMemory)
            {
                // Memory is faster in situations where we can get away with it.
                statement = $"memory.{nameof(InputBuffer.ReadMemoryBlock)}<{CSharpHelpers.GetCompilableTypeName(itemTypeModel.ClrType)}>(offset, {itemTypeModel.InlineSize}).ToArray()";
            }
            else
            {
                statement = $"{this.CreateFlatBufferVector(typeModel)}.ToArray()";
            }

            this.GenerateMethodDefinition(typeModel.ClrType, $"return {statement};");
        }

        private void ImplementEnumReadMethod(EnumTypeModel typeModel)
        {
            Type enumType = typeModel.ClrType;
            Type underlyingType = Enum.GetUnderlyingType(enumType);

            string body = $"return ({CSharpHelpers.GetCompilableTypeName(enumType)}){this.GetReadInvocation(underlyingType, "memory", "offset")};";
            this.GenerateMethodDefinition(enumType, body);
        }

        private string CreateFlatBufferVector(VectorTypeModel typeModel)
        {
            // Params: Buffer, UOffset after following, Padded size of each member, delegate invocation for parsing individual items.
            return $@"new {nameof(FlatBufferVector<byte>)}<{CSharpHelpers.GetCompilableTypeName(typeModel.ItemTypeModel.ClrType)}>(
                    memory, 
                    offset + memory.{nameof(InputBuffer.ReadUOffset)}(offset), 
                    {typeModel.PaddedMemberInlineSize}, 
                    (b, o) => {this.GetReadInvocation(typeModel.ItemTypeModel.ClrType, "b", "o")})";
        }

        private string GetReadInvocation(Type type, string buffer, string offset)
        {
            if (BuiltInType.BuiltInTypes.TryGetValue(type, out var builtInType))
            {
                return $"{buffer}.{builtInType.InputBufferRead.Name}({offset})";
            }
            else
            {
                return $"{this.MethodNames[type]}({buffer}, {offset})";
            }
        }

        private void GenerateMethodDefinition(Type type, string body)
        {
            string methodDef =
$@"
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static {CSharpHelpers.GetCompilableTypeName(type)} {this.MethodNames[type]} (InputBuffer memory, int offset)
            {{
                {body}
            }}
";

            var node = CSharpSyntaxTree.ParseText(methodDef, ParseOptions);
            this.methodDeclarations.Add(node.GetRoot());
        }
    }
}
