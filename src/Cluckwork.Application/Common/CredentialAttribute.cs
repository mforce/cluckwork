namespace Cluckwork.Application.Common;

// G3.29 (#1397): marks a type that holds a credential under a member name the
// redaction guard does not read as one, such as Value. The guard then requires
// the type and every subtype to print only "[redacted]".
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class CredentialAttribute : Attribute;
