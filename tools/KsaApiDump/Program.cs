using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Dumps type/member names from a managed assembly without loading it.
// Usage: KsaApiDump <dllPath> [typeNameFilter] [--methods]
var dllPath = args[0];
var filter = args.Length > 1 ? args[1] : "";
var showMembers = args.Contains("--methods");

using var fs = File.OpenRead(dllPath);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
var typeProvider = new SigTypeProvider(md);

foreach (var typeHandle in md.TypeDefinitions)
{
    var type = md.GetTypeDefinition(typeHandle);
    var name = md.GetString(type.Name);
    var ns = md.GetString(type.Namespace);
    var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

    if (!fullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
        continue;

    var kind = "class";
    var baseHandle = type.BaseType;
    var baseFullName = "";
    if (!baseHandle.IsNil)
    {
        switch (baseHandle.Kind)
        {
            case HandleKind.TypeReference:
                var tr = md.GetTypeReference((TypeReferenceHandle)baseHandle);
                var trNs = md.GetString(tr.Namespace);
                baseFullName = string.IsNullOrEmpty(trNs) ? md.GetString(tr.Name) : trNs + "." + md.GetString(tr.Name);
                break;
            case HandleKind.TypeDefinition:
                var td = md.GetTypeDefinition((TypeDefinitionHandle)baseHandle);
                var tdNs = md.GetString(td.Namespace);
                baseFullName = string.IsNullOrEmpty(tdNs) ? md.GetString(td.Name) : tdNs + "." + md.GetString(td.Name);
                break;
        }
        var baseName = baseFullName.Split('.').Last();
        if (baseName is "ValueType" or "Enum") kind = "struct";
    }
    var isByRefLike = type.GetCustomAttributes().Any(h =>
    {
        var a = md.GetCustomAttribute(h);
        if (a.Constructor.Kind != HandleKind.MemberReference) return false;
        var mr = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
        if (mr.Parent.Kind != HandleKind.TypeReference) return false;
        return md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name) == "IsByRefLikeAttribute";
    });
    if (isByRefLike) kind = "ref struct";

    Console.WriteLine($"TYPE {fullName} [{kind}] : {baseFullName}");

    if (showMembers)
    {
        foreach (var fieldHandle in type.GetFields())
        {
            var field = md.GetFieldDefinition(fieldHandle);
            var sig = "";
            try
            {
                var decoder = new SignatureDecoder<string, int>(typeProvider, md, 0);
                var blob = md.GetBlobReader(field.Signature);
                sig = " : " + decoder.DecodeFieldSignature(ref blob);
            }
            catch { }
            Console.WriteLine($"  FIELD {md.GetString(field.Name)}{sig}");
        }
        foreach (var propHandle in type.GetProperties())
        {
            var prop = md.GetPropertyDefinition(propHandle);
            Console.WriteLine($"  PROP {md.GetString(prop.Name)}");
        }
        foreach (var methodHandle in type.GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            var paramNames = method.GetParameters()
                .Select(h => md.GetParameter(h))
                .Where(p => p.SequenceNumber > 0)
                .Select(p => md.GetString(p.Name))
                .ToArray();

            string signature;
            try
            {
                var decoder = new SignatureDecoder<string, int>(typeProvider, md, 0);
                var blob = md.GetBlobReader(method.Signature);
                var msig = decoder.DecodeMethodSignature(ref blob);
                var parts = msig.ParameterTypes
                    .Select((t, i) => $"{t} {(i < paramNames.Length ? paramNames[i] : "?")}");
                signature = $"({string.Join(", ", parts)}) -> {msig.ReturnType}";
            }
            catch
            {
                signature = $"({string.Join(", ", paramNames)})";
            }

            Console.WriteLine($"  METHOD {md.GetString(method.Name)}{signature}");
        }
    }
}

sealed class SigTypeProvider : ISignatureTypeProvider<string, int>
{
    private readonly MetadataReader _md;
    public SigTypeProvider(MetadataReader md) => _md = md;

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(int genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(int genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => TypeName(reader.GetTypeDefinition(handle));
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        => TypeName(reader.GetTypeReference(handle));
    public string GetTypeFromSpecification(MetadataReader reader, int genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => "typespec";
    public string GetTupleType(ImmutableArray<string> elementTypes) => "(" + string.Join(",", elementTypes) + ")";

    private string TypeName(TypeDefinition t)
    {
        var n = _md.GetString(t.Name);
        var ns = _md.GetString(t.Namespace);
        return string.IsNullOrEmpty(ns) ? n : n; // short name only
    }

    private string TypeName(TypeReference t)
    {
        var n = _md.GetString(t.Name);
        return n;
    }
}
