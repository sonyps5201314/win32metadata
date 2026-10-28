$ErrorActionPreference = 'Stop'
function AttributeName($reader, $attribute) {
    $ctor = $attribute.Constructor
    if ($ctor.Kind -eq 'MemberReference') {
        $parent = $reader.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$ctor).Parent
    } else {
        $parent = $reader.GetMethodDefinition([Reflection.Metadata.MethodDefinitionHandle]$ctor).GetDeclaringType()
    }
    if ($parent.Kind -eq 'TypeReference') {
        $type = $reader.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$parent)
    } else {
        $type = $reader.GetTypeDefinition([Reflection.Metadata.TypeDefinitionHandle]$parent)
    }
    return $reader.GetString($type.Name)
}
function Attributes($reader, $handles) {
    $values = @{}
    foreach ($handle in $handles) {
        $attribute = $reader.GetCustomAttribute($handle)
        $name = AttributeName $reader $attribute
        if ($name -in 'GuidAttribute','SupportedArchitectureAttribute') {
            $values[$name] = [Convert]::ToHexString($reader.GetBlobBytes($attribute.Value))
        }
    }
    return ,$values
}
function Snapshot($path) {
    $stream = [IO.File]::OpenRead($path)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $values = @{}
        foreach ($handle in $reader.TypeDefinitions) {
            $type = $reader.GetTypeDefinition($handle)
            $owner = $reader.GetString($type.Namespace) + '.' + $reader.GetString($type.Name)
            $typeAttrs = Attributes $reader $type.GetCustomAttributes()
            foreach ($fieldHandle in $type.GetFields()) {
                $field = $reader.GetFieldDefinition($fieldHandle)
                $attrs = Attributes $reader $field.GetCustomAttributes()
                $constantHandle = $field.GetDefaultValue()
                if ($constantHandle.IsNil -and !$attrs.ContainsKey('GuidAttribute')) { continue }
                $key = $owner + '.' + $reader.GetString($field.Name) + ':' + $typeAttrs['SupportedArchitectureAttribute'] + ':' + $attrs['SupportedArchitectureAttribute']
                if ($values.ContainsKey($key)) { throw "重复常量: $key" }
                if ($constantHandle.IsNil) {
                    $values[$key] = 'Guid:' + $attrs['GuidAttribute']
                } else {
                    $constant = $reader.GetConstant($constantHandle)
                    $values[$key] = 'Literal:' + $constant.TypeCode + ':' + [Convert]::ToHexString($reader.GetBlobBytes($constant.Value))
                }
            }
        }
        return ,@{ Values=$values; Types=$reader.TypeDefinitions.Count; Fields=$reader.FieldDefinitions.Count; Methods=$reader.MethodDefinitions.Count }
    } finally { $pe.Dispose(); $stream.Dispose() }
}
$before = Snapshot "$PSScriptRoot\before.winmd"
$after = Snapshot "$PSScriptRoot\after.winmd"
$old = $before.Values
$new = $after.Values
$report = @{
    BeforeLiteral = @($old.Values | Where-Object { $_.StartsWith('Literal:') }).Count
    AfterLiteral = @($new.Values | Where-Object { $_.StartsWith('Literal:') }).Count
    BeforeGuid = @($old.Values | Where-Object { $_.StartsWith('Guid:') }).Count
    AfterGuid = @($new.Values | Where-Object { $_.StartsWith('Guid:') }).Count
    Missing = @($old.Keys | Where-Object { !$new.ContainsKey($_) })
    Added = @($new.Keys | Where-Object { !$old.ContainsKey($_) })
    Changed = @($old.Keys | Where-Object { $new.ContainsKey($_) -and $new[$_] -cne $old[$_] })
    BeforeSchema = @{Types=$before.Types; Fields=$before.Fields; Methods=$before.Methods}
    AfterSchema = @{Types=$after.Types; Fields=$after.Fields; Methods=$after.Methods}
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$PSScriptRoot\constants-comparison.json"
$report | ConvertTo-Json -Depth 5
if ($report.Missing.Count -or $report.Added.Count -or $report.Changed.Count) { throw '常量变化' }
