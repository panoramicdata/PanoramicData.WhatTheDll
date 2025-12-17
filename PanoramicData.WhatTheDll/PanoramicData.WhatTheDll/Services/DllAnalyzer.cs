using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace PanoramicData.WhatTheDll.Services;

public class DllAnalyzer
{
    public AssemblyInfo AnalyzeAssembly(byte[] assemblyBytes)
    {
        var info = new AssemblyInfo();

        try
        {
            using var stream = new MemoryStream(assemblyBytes);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                info.Error = "File does not contain valid metadata";
                return info;
            }

            var metadataReader = peReader.GetMetadataReader();

            // Basic assembly info
            var assemblyDef = metadataReader.GetAssemblyDefinition();
            info.Name = metadataReader.GetString(assemblyDef.Name);
            info.Version = assemblyDef.Version.ToString();
            info.Culture = metadataReader.GetString(assemblyDef.Culture);
            
            // Public key token
            var publicKey = metadataReader.GetBlobBytes(assemblyDef.PublicKey);
            if (publicKey.Length > 0)
            {
                using var sha1 = System.Security.Cryptography.SHA1.Create();
                var hash = sha1.ComputeHash(publicKey);
                var token = new byte[8];
                Array.Copy(hash, hash.Length - 8, token, 0, 8);
                Array.Reverse(token);
                info.PublicKeyToken = BitConverter.ToString(token).Replace("-", "").ToLowerInvariant();
            }
            
            // PE header info
            var peHeaders = peReader.PEHeaders;
            info.Architecture = peHeaders.CoffHeader.Machine switch
            {
                Machine.I386 => "x86",
                Machine.Amd64 => "x64",
                Machine.Arm => "ARM",
                Machine.Arm64 => "ARM64",
                _ => peHeaders.CoffHeader.Machine.ToString()
            };
            
            info.Subsystem = peHeaders.PEHeader?.Subsystem switch
            {
                Subsystem.WindowsCui => "Console",
                Subsystem.WindowsGui => "Windows GUI",
                _ => peHeaders.PEHeader?.Subsystem.ToString()
            };
            
            // Runtime version from metadata
            info.RuntimeVersion = metadataReader.MetadataVersion;
            
            // Extract custom attributes
            ExtractCustomAttributes(metadataReader, info);

            foreach (var typeDefHandle in metadataReader.TypeDefinitions)
            {
                var typeDef = metadataReader.GetTypeDefinition(typeDefHandle);
                var typeName = metadataReader.GetString(typeDef.Name);
                var namespaceName = metadataReader.GetString(typeDef.Namespace);

                if (string.IsNullOrEmpty(namespaceName) || typeName.StartsWith("<"))
                    continue;

                var fullName = string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
                var typeInfo = new TypeInfo
                {
                    Name = typeName,
                    Namespace = namespaceName,
                    FullName = fullName,
                    IsPublic = (typeDef.Attributes & TypeAttributes.Public) == TypeAttributes.Public,
                    IsClass = (typeDef.Attributes & TypeAttributes.Interface) != TypeAttributes.Interface &&
                              (typeDef.Attributes & TypeAttributes.Class) == TypeAttributes.Class,
                    IsInterface = (typeDef.Attributes & TypeAttributes.Interface) == TypeAttributes.Interface,
                    IsAbstract = (typeDef.Attributes & TypeAttributes.Abstract) == TypeAttributes.Abstract,
                    IsSealed = (typeDef.Attributes & TypeAttributes.Sealed) == TypeAttributes.Sealed,
                    MethodToken = typeDef.GetMethods().FirstOrDefault().IsNil ? 0 : MetadataTokens.GetToken(typeDef.GetMethods().First())
                };

                // Build a dictionary of property accessors to exclude from methods
                var propertyAccessors = new HashSet<MethodDefinitionHandle>();
                var propertyInfos = new Dictionary<string, PropertyInfo>();

                foreach (var propertyHandle in typeDef.GetProperties())
                {
                    var property = metadataReader.GetPropertyDefinition(propertyHandle);
                    var propertyName = metadataReader.GetString(property.Name);
                    var accessors = property.GetAccessors();
                    
                    var propInfo = new PropertyInfo
                    {
                        Name = propertyName,
                        HasGetter = !accessors.Getter.IsNil,
                        HasSetter = !accessors.Setter.IsNil
                    };
                    
                    // Get property type from getter or setter signature
                    if (!accessors.Getter.IsNil)
                    {
                        propertyAccessors.Add(accessors.Getter);
                        var getterDef = metadataReader.GetMethodDefinition(accessors.Getter);
                        propInfo.IsPublic = (getterDef.Attributes & MethodAttributes.Public) == MethodAttributes.Public;
                        propInfo.IsStatic = (getterDef.Attributes & MethodAttributes.Static) == MethodAttributes.Static;
                        propInfo.Type = DecodeReturnType(metadataReader, getterDef);
                    }
                    if (!accessors.Setter.IsNil)
                    {
                        propertyAccessors.Add(accessors.Setter);
                        if (!propInfo.IsPublic)
                        {
                            var setterDef = metadataReader.GetMethodDefinition(accessors.Setter);
                            propInfo.IsPublic = (setterDef.Attributes & MethodAttributes.Public) == MethodAttributes.Public;
                        }
                        if (string.IsNullOrEmpty(propInfo.Type))
                        {
                            var setterDef = metadataReader.GetMethodDefinition(accessors.Setter);
                            propInfo.IsStatic = (setterDef.Attributes & MethodAttributes.Static) == MethodAttributes.Static;
                            // Get type from setter parameter
                            var setterParams = DecodeParameters(metadataReader, setterDef);
                            if (setterParams.Any())
                            {
                                propInfo.Type = setterParams.Last().Type;
                            }
                        }
                    }
                    
                    typeInfo.Properties.Add(propInfo);
                }

                foreach (var methodDefHandle in typeDef.GetMethods())
                {
                    var methodDef = metadataReader.GetMethodDefinition(methodDefHandle);
                    var methodName = metadataReader.GetString(methodDef.Name);

                    // Skip compiler-generated, constructors, and property accessors
                    if (methodName.StartsWith("<") || methodName.StartsWith("."))
                        continue;
                    if (propertyAccessors.Contains(methodDefHandle))
                        continue;

                    var returnType = DecodeReturnType(metadataReader, methodDef);
                    var isAsync = returnType.StartsWith("Task") || returnType.StartsWith("ValueTask");
                    
                    var methodInfo = new MethodInfo
                    {
                        Name = methodName,
                        ReturnType = returnType,
                        Parameters = DecodeParameters(metadataReader, methodDef),
                        IsPublic = (methodDef.Attributes & MethodAttributes.Public) == MethodAttributes.Public,
                        IsStatic = (methodDef.Attributes & MethodAttributes.Static) == MethodAttributes.Static,
                        IsAbstract = (methodDef.Attributes & MethodAttributes.Abstract) == MethodAttributes.Abstract,
                        IsVirtual = (methodDef.Attributes & MethodAttributes.Virtual) == MethodAttributes.Virtual,
                        IsAsync = isAsync,
                        MethodToken = MetadataTokens.GetToken(methodDefHandle)
                    };

                    typeInfo.Methods.Add(methodInfo);
                }

                info.Types.Add(typeInfo);
            }

            foreach (var assemblyRefHandle in metadataReader.AssemblyReferences)
            {
                var assemblyRef = metadataReader.GetAssemblyReference(assemblyRefHandle);
                var refName = metadataReader.GetString(assemblyRef.Name);
                info.References.Add($"{refName}, Version={assemblyRef.Version}");
            }
        }
        catch (Exception ex)
        {
            info.Error = $"Error analyzing assembly: {ex.Message}";
        }

        return info;
    }
    
    private string DecodeReturnType(MetadataReader reader, MethodDefinition methodDef)
    {
        try
        {
            var signature = methodDef.DecodeSignature(new TypeNameProvider(reader), null);
            return signature.ReturnType;
        }
        catch
        {
            return "?";
        }
    }
    
    private List<ParameterInfo> DecodeParameters(MetadataReader reader, MethodDefinition methodDef)
    {
        var result = new List<ParameterInfo>();
        try
        {
            var signature = methodDef.DecodeSignature(new TypeNameProvider(reader), null);
            var parameterHandles = methodDef.GetParameters().ToList();
            
            for (int i = 0; i < signature.ParameterTypes.Length; i++)
            {
                var paramName = "arg" + i;
                
                // Try to get parameter name from metadata
                foreach (var handle in parameterHandles)
                {
                    var param = reader.GetParameter(handle);
                    if (param.SequenceNumber == i + 1) // Parameters are 1-indexed
                    {
                        paramName = reader.GetString(param.Name);
                        break;
                    }
                }
                
                result.Add(new ParameterInfo
                {
                    Name = paramName,
                    Type = signature.ParameterTypes[i]
                });
            }
        }
        catch
        {
            // Ignore signature decoding errors
        }
        return result;
    }
    
    private void ExtractCustomAttributes(MetadataReader metadataReader, AssemblyInfo info)
    {
        foreach (var attrHandle in metadataReader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attr = metadataReader.GetCustomAttribute(attrHandle);
            var ctorHandle = attr.Constructor;
            
            string? attrTypeName = null;
            if (ctorHandle.Kind == HandleKind.MemberReference)
            {
                var memberRef = metadataReader.GetMemberReference((MemberReferenceHandle)ctorHandle);
                var parentHandle = memberRef.Parent;
                if (parentHandle.Kind == HandleKind.TypeReference)
                {
                    var typeRef = metadataReader.GetTypeReference((TypeReferenceHandle)parentHandle);
                    attrTypeName = metadataReader.GetString(typeRef.Name);
                }
            }
            else if (ctorHandle.Kind == HandleKind.MethodDefinition)
            {
                var methodDef = metadataReader.GetMethodDefinition((MethodDefinitionHandle)ctorHandle);
                var declaringType = metadataReader.GetTypeDefinition(methodDef.GetDeclaringType());
                attrTypeName = metadataReader.GetString(declaringType.Name);
            }
            
            if (attrTypeName == null) continue;
            
            var value = GetAttributeStringValue(metadataReader, attr);
            
            switch (attrTypeName)
            {
                case "TargetFrameworkAttribute":
                    info.TargetFramework = value;
                    break;
                case "AssemblyCompanyAttribute":
                    info.Company = value;
                    break;
                case "AssemblyProductAttribute":
                    info.Product = value;
                    break;
                case "AssemblyDescriptionAttribute":
                    info.Description = value;
                    break;
                case "AssemblyCopyrightAttribute":
                    info.Copyright = value;
                    break;
                case "AssemblyFileVersionAttribute":
                    info.FileVersion = value;
                    break;
                case "AssemblyInformationalVersionAttribute":
                    info.InformationalVersion = value;
                    break;
                case "AssemblyConfigurationAttribute":
                    info.Configuration = value;
                    if (value?.ToLowerInvariant() == "debug")
                        info.IsDebug = true;
                    break;
                case "DebuggableAttribute":
                    info.IsDebug = true;
                    break;
            }
        }
    }
    
    private string? GetAttributeStringValue(MetadataReader reader, CustomAttribute attr)
    {
        try
        {
            var valueBytes = reader.GetBlobBytes(attr.Value);
            if (valueBytes.Length < 4) return null;
            
            // Custom attribute blob format: 2-byte prolog (0x0001), then values
            if (valueBytes[0] != 0x01 || valueBytes[1] != 0x00)
                return null;
            
            // Read string length (packed unsigned integer)
            int offset = 2;
            if (offset >= valueBytes.Length) return null;
            
            byte firstByte = valueBytes[offset];
            if (firstByte == 0xFF) return null; // null string
            
            int length;
            if ((firstByte & 0x80) == 0)
            {
                length = firstByte;
                offset += 1;
            }
            else if ((firstByte & 0xC0) == 0x80)
            {
                if (offset + 1 >= valueBytes.Length) return null;
                length = ((firstByte & 0x3F) << 8) | valueBytes[offset + 1];
                offset += 2;
            }
            else
            {
                if (offset + 3 >= valueBytes.Length) return null;
                length = ((firstByte & 0x1F) << 24) | (valueBytes[offset + 1] << 16) | 
                         (valueBytes[offset + 2] << 8) | valueBytes[offset + 3];
                offset += 4;
            }
            
            if (offset + length > valueBytes.Length) return null;
            
            return System.Text.Encoding.UTF8.GetString(valueBytes, offset, length);
        }
        catch
        {
            return null;
        }
    }

    public PdbLoadResult EnhanceWithPdbInfo(AssemblyInfo assemblyInfo, byte[] assemblyBytes, byte[] pdbBytes)
    {
        try
        {
            using var pdbStream = new MemoryStream(pdbBytes);
            using var pdbReaderProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var pdbReader = pdbReaderProvider.GetMetadataReader();

            // Extract PDB GUID from the PDB ID header
            var pdbId = pdbReader.DebugMetadataHeader?.Id;
            if (pdbId.HasValue && pdbId.Value.Length >= 16)
            {
                var guidBytes = new byte[16];
                pdbId.Value.CopyTo(0, guidBytes, 0, 16);
                assemblyInfo.PdbGuid = new Guid(guidBytes).ToString("D");
            }
            
            // Count unique source files
            var sourceFiles = new HashSet<string>();
            
            // Build a dictionary of method tokens to source info
            var methodSourceInfo = new Dictionary<int, (string SourceFile, int LineNumber)>();

            foreach (var methodDebugInfoHandle in pdbReader.MethodDebugInformation)
            {
                var methodDebugInfo = pdbReader.GetMethodDebugInformation(methodDebugInfoHandle);
                
                if (methodDebugInfo.Document.IsNil)
                    continue;

                var document = pdbReader.GetDocument(methodDebugInfo.Document);
                var sourceFile = pdbReader.GetString(document.Name);
                sourceFiles.Add(sourceFile);

                // Get the first sequence point to find the starting line number
                var sequencePoints = methodDebugInfo.GetSequencePoints();
                int lineNumber = 0;
                foreach (var sp in sequencePoints)
                {
                    if (!sp.IsHidden)
                    {
                        lineNumber = sp.StartLine;
                        break;
                    }
                }

                // The handle row number corresponds to the method token
                int methodToken = MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(methodDebugInfoHandle)));
                methodSourceInfo[methodToken] = (sourceFile, lineNumber);
            }
            
            assemblyInfo.SourceFilesCount = sourceFiles.Count;

            // Now update the assembly info with source information
            foreach (var typeInfo in assemblyInfo.Types)
            {
                // Set source file for the type based on the first method with source info
                foreach (var method in typeInfo.Methods)
                {
                    if (methodSourceInfo.TryGetValue(method.MethodToken, out var sourceInfo))
                    {
                        method.SourceFile = sourceInfo.SourceFile;
                        method.LineNumber = sourceInfo.LineNumber;
                        
                        // Set the type's source file to the first method's source file
                        if (string.IsNullOrEmpty(typeInfo.SourceFile))
                        {
                            typeInfo.SourceFile = sourceInfo.SourceFile;
                        }
                    }
                }
            }

            return new PdbLoadResult { Success = true };
        }
        catch (Exception ex)
        {
            return new PdbLoadResult 
            { 
                Success = false, 
                Error = $"Error loading PDB: {ex.Message}. Make sure it's a portable PDB file that matches the assembly." 
            };
        }
    }
}

public class AssemblyInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Culture { get; set; } = "";
    public string? PublicKeyToken { get; set; }
    public string? TargetFramework { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? Architecture { get; set; }
    public string? Subsystem { get; set; }
    public bool IsDebug { get; set; }
    
    // Assembly attributes
    public string? Company { get; set; }
    public string? Product { get; set; }
    public string? Description { get; set; }
    public string? Copyright { get; set; }
    public string? FileVersion { get; set; }
    public string? InformationalVersion { get; set; }
    public string? Configuration { get; set; }
    
    // PDB info
    public string? PdbGuid { get; set; }
    public int SourceFilesCount { get; set; }
    
    public List<TypeInfo> Types { get; set; } = new();
    public List<string> References { get; set; } = new();
    public string? Error { get; set; }
}

public class TypeInfo
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string FullName { get; set; } = "";
    public bool IsPublic { get; set; }
    public bool IsClass { get; set; }
    public bool IsInterface { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsSealed { get; set; }
    public List<MethodInfo> Methods { get; set; } = new();
    public List<PropertyInfo> Properties { get; set; } = new();
    public string? SourceFile { get; set; }
    public int MethodToken { get; set; }
}

public class PropertyInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool HasGetter { get; set; }
    public bool HasSetter { get; set; }
    public bool IsPublic { get; set; }
    public bool IsStatic { get; set; }
}

public class MethodInfo
{
    public string Name { get; set; } = "";
    public string ReturnType { get; set; } = "void";
    public List<ParameterInfo> Parameters { get; set; } = new();
    public bool IsPublic { get; set; }
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsAsync { get; set; }
    public string? SourceFile { get; set; }
    public int LineNumber { get; set; }
    public int MethodToken { get; set; }
    
    public string GetSignature()
    {
        var parameters = string.Join(", ", Parameters.Select(p => $"{p.Type} {p.Name}"));
        return $"{Name}({parameters})";
    }
    
    public string GetShortSignature()
    {
        var parameters = string.Join(", ", Parameters.Select(p => p.Type));
        return $"{Name}({parameters})";
    }
}

public class ParameterInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

public class PdbLoadResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Provides human-readable type names from metadata signatures
/// </summary>
public class TypeNameProvider : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader _reader;
    
    public TypeNameProvider(MetadataReader reader)
    {
        _reader = reader;
    }
    
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString()
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var typeDef = reader.GetTypeDefinition(handle);
        return reader.GetString(typeDef.Name);
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var typeRef = reader.GetTypeReference(handle);
        var name = reader.GetString(typeRef.Name);
        
        // Simplify common types
        return name switch
        {
            "Task" => "Task",
            "ValueTask" => "ValueTask",
            "Nullable`1" => "?",
            _ => name.Contains('`') ? name.Substring(0, name.IndexOf('`')) : name
        };
    }

    public string GetSZArrayType(string elementType) => $"{elementType}[]";
    
    public string GetArrayType(string elementType, ArrayShape shape)
    {
        var rank = new string(',', shape.Rank - 1);
        return $"{elementType}[{rank}]";
    }

    public string GetByReferenceType(string elementType) => $"ref {elementType}";
    
    public string GetPointerType(string elementType) => $"{elementType}*";
    
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        if (genericType == "?" && typeArguments.Length == 1)
        {
            return $"{typeArguments[0]}?";
        }
        
        var args = string.Join(", ", typeArguments);
        return $"{genericType}<{args}>";
    }

    public string GetGenericMethodParameter(object? genericContext, int index) => $"T{index}";
    
    public string GetGenericTypeParameter(object? genericContext, int index) => $"T{index}";
    
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    
    public string GetPinnedType(string elementType) => elementType;
    
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
        var typeSpec = reader.GetTypeSpecification(handle);
        return typeSpec.DecodeSignature(this, genericContext);
    }

    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";
}
