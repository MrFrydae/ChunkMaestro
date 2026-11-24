namespace ChunkMaestro.PrivilegedHelper;

internal enum PolicyParseCode
{
    EmptyInput,
    InputTooLarge,
    InvalidEncoding,
    MalformedJson,
    DepthExceeded,
    TrailingData,
    UnknownField,
    DuplicateField,
    MissingField,
    WrongType,
    InvalidNumberLiteral,
    NumberOutOfRange,
    UnsupportedSchema,
    InvalidIdentifier,
    InvalidDigest,
    InvalidPath,
    InvalidUnitName,
    IdentityCollision,
    InvalidBinding,
    DuplicateBinding,
    UnorderedDropIns,
    CollectionLimit
}
