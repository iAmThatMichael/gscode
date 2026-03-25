# GSC Function Registry — Schema Reference

## Overview

This document describes the JSON schema output by `gsc_func_registry.py`, an IDAPython script that extracts GSC (Game Script Code) function and method signatures from a Call of Duty binary via static analysis of IDA Pro decompiler output.

The registry is a JSON array of entry objects. Each entry represents one script-callable function or method.

---

## Top-Level Structure

```json
[
  { /* entry */ },
  { /* entry */ },
  ...
]
```

---

## Entry Schema

```json
{
  "name": "getdvarint",
  "kind": "function",
  "min_args": 1,
  "max_args": 2,
  "handler": "BGScr_GetDvarInt",
  "handler_addr": "0x1402ab3c0",
  "parameters": [
    { "index": 0, "type": "hash | int | string" },
    { "index": 1, "type": "int" }
  ],
  "return_type": "int",
  "return_types": ["int"],
  "return_subtypes": [],
  "overload_param_counts": [2],
  "error_strings": ["Optional argument must be a integer type"]
}
```

### Field Definitions

| Field | Type | Description |
|-------|------|-------------|
| `name` | `string` | Lowercase GSC function name as it appears in script (e.g. `"spawnfx"`, `"getent"`). |
| `kind` | `"function"` or `"method"` | `"function"` = standalone global call. `"method"` = called on an entity/object via `ent.methodName()` syntax. Methods take an implicit `self` (the entity reference) that is **not** listed in `parameters`. |
| `min_args` | `int` or `null` | Minimum required argument count from the registration table. `null` if extraction failed. |
| `max_args` | `int` or `null` | Maximum allowed argument count from the registration table. `null` if extraction failed. |
| `handler` | `string` or `null` | C function name that implements this script function (e.g. `"Scr_SpawnFX"`). Useful for cross-referencing back to the binary. |
| `handler_addr` | `string` or `null` | Hex address of the handler in the binary (e.g. `"0x1402ab3c0"`). |
| `called_on` | `string` or `null` | **Methods only.** What entity class this method is called on. See "Entity Subtypes" below. `null` if method target could not be determined. |
| `parameters` | `array` | Ordered list of parameter objects. See "Parameters" below. |
| `return_type` | `string` | Canonical return type. Single type string, `"void"` if no return, or `" \| "` separated union if multiple return types are possible. See "Type Strings" below. |
| `return_types` | `array of string` | Raw list of all distinct return types detected (before canonicalization). |
| `return_subtypes` | `array of string` | Entity subtypes detected from `Scr_AddEntity` 3rd argument. Only populated when the return involves entities. See "Entity Subtypes". |
| `overload_param_counts` | `array of int` | Values seen in `Scr_GetNumParam()` comparisons. Indicates which argument counts trigger different code paths / overloads. |
| `error_strings` | `array of string` | Error messages found in the handler (from `Scr_ParamError`, `Scr_Error`, `va()`). Useful for documentation and understanding constraints. |

---

## Parameters

```json
{ "index": 0, "type": "string" }
{ "index": 0, "type": "hash | int | string" }
```

| Field | Type | Description |
|-------|------|-------------|
| `index` | `int` | Zero-based parameter index. Index 0 is the first explicit argument in GSC. For methods, this does **not** include the implicit `self` entity. |
| `type` | `string` | Inferred type. May be a single type (`"string"`) or a union of types separated by `" \| "` (`"hash \| int \| string"`). See "Type Strings" below. |

### Union Types in Parameters

When a handler reads the same parameter index with multiple different typed getters (typically after a `Scr_GetType` branch), the parameter type is a `" | "` separated union. The types within the union are sorted alphabetically for deterministic output.

For example, `BGScr_GetDvarInt` checks if param 0 is `SCR_TYPE_STRING`, `SCR_TYPE_INT`, or `SCR_TYPE_HASH`, then calls the appropriate getter in each branch. This produces:

```json
{ "index": 0, "type": "hash | int | string" }
```

**Subsumption rules applied during merging:**
- If both `"int"` and `"float"` appear for the same parameter, they collapse to `"number"` (since both are numeric and `Scr_GetFloat` accepts ints anyway).
- All other combinations remain as explicit unions.

### Notes on Parameters

- **Not all parameters may be listed.** If an argument is consumed by a function the extractor doesn't recognise, or passed through without a typed getter, it will be missing.
- **Optional parameters** are detected via `overload_param_counts`. If `min_args=2` and `max_args=4`, params at index 2 and 3 are optional.
- **Type check authority.** When a handler uses `Scr_GetType()` or `Scr_GetPointerType()` to branch before calling different getters, the type check values are treated as authoritative — they represent the full set of types the handler accepts.

---

## Type Strings

These are the possible atomic values that appear in parameter and return types, either standalone or as part of a `" | "` union.

### Core Types

| Type String | GSC Equivalent | Notes |
|-------------|---------------|-------|
| `"void"` | (no return) | Function does not push a return value. Only appears as `return_type`, never in parameters. |
| `"any"` | any | Type could not be determined, or `Scr_AddValue` was used (dynamic type at runtime). |
| `"undefined"` | undefined | Explicitly returns/accepts undefined. |
| `"bool"` | bool | Detected via `Scr_GetBool` or the pattern `Scr_GetInt(inst, N) != 0`. |
| `"int"` | int | Integer value. If it was used as `!= 0` in the source, it's been promoted to `"bool"` instead. |
| `"float"` | float | Floating point. **Rarely appears as a parameter type** — see `"number"` below. May still appear as a return type from `Scr_AddFloat`. |
| `"number"` | int \| float | Numeric value accepting both int and float. This is the parameter type for `Scr_GetFloat`, which internally coerces ints to float. Also produced when the subsumption rule merges `"int"` and `"float"` on the same parameter. |
| `"string"` | string | String value. Includes `Scr_GetString`, `Scr_GetConstString`, and `Scr_GetStringOptional`. |
| `"istring"` | localized string | Localized/interned string (e.g. `&"STRING_REF"`). |
| `"vector"` | vector | 3-component vector `(x, y, z)`. |
| `"array"` | array | Array. Detected via `Scr_GetObject` + `Scr_GetPointerType == SCR_TYPE_ARRAY` check, `BGScr_GetArrayObject`, or `Scr_AddArray` / `Scr_MakeArray`. |
| `"struct"` | struct | Script struct (key-value pairs). Detected via `Scr_GetStruct` or pointer type checks against `SCR_TYPE_STRUCT` / `SCR_TYPE_SHARED_STRUCT` (enum values `0x17` / `0x16`). |
| `"object"` | object | Generic script object. **Ambiguous type** — comes from `Scr_GetObject` / `Scr_AddObject` where no pointer type check was found to refine it. Could be array, struct, entity, or other reference type. Treat as "unknown reference type". |
| `"entity"` | entity | Game entity. May have a subtype (see "Entity Subtypes"). |
| `"weapon"` | weapon | Weapon reference. From `Scr_AddWeapon` or `SCR_CLASS_WEAPON`. |
| `"function"` | function | Function pointer / code reference. |
| `"anim"` | anim | Animation reference. |
| `"animtree"` | animtree | Animation tree reference. |
| `"hash"` | hash | Hash value. Enum value `SCR_TYPE_HASH (0x05)`. Appears in unions when handlers accept hashed identifiers alongside strings/ints. |
| `"uint64"` | uint64 | Unsigned 64-bit integer. Rare. |
| `"team"` | team | Team identifier. From `Scr_GetTeam`. At runtime effectively a string/enum, kept distinct for clarity. |

### Type Differences: Parameters vs Returns

- **`"number"`** appears in parameters (from `Scr_GetFloat` which accepts int or float) but not in returns. Return types use the specific `"int"` or `"float"` from the corresponding `Scr_AddInt` / `Scr_AddFloat`.
- **`"void"`** only appears as a `return_type`, never in parameters.
- **Union types** (`" | "`) can appear in both parameters and return types.

### Confidence Levels

Not all type extractions are equally reliable:

- **High confidence:** `Scr_GetString`, `Scr_GetVector`, `Scr_GetBool`, `Scr_GetEntity`, `Scr_GetFloat` (→ `"number"`), `Scr_AddInt`, `Scr_AddVector`, etc. These are unambiguous typed getters/adders.
- **High confidence:** Union types derived from explicit `Scr_GetType` / `Scr_GetPointerType` branching. The handler explicitly checks and branches, so the union is authoritative.
- **Medium confidence:** `"bool"` detected via `Scr_GetInt(inst, N) != 0`. This is a heuristic — the code treats the int as a boolean, but technically it's still an int parameter. In practice this is almost always correct.
- **Medium confidence:** `"array"` / `"struct"` detected via pointer type checks. Reliable when the check is present, but the parameter might accept other types in branches we didn't trace.
- **Low confidence:** `"object"` — means we saw `Scr_GetObject` or `Scr_AddObject` but couldn't determine what it actually is. Could be array, struct, or something else.
- **Low confidence:** `"any"` — from `Scr_AddValue` (runtime-dynamic type) or unresolved type checks. The actual type depends on runtime state.

---

## Entity Subtypes

Entities have subclasses determined by `ScrClassId`, which is the 3rd argument to `Scr_AddEntity()`. These appear in `return_subtypes` and `called_on`:

| Subtype String | ScrClassId | Description |
|----------------|-----------|-------------|
| `"entity"` | `SCR_CLASS_ENTITY (0)` | Generic entity (players, actors, script objects, etc.) |
| `"entity:hudelem"` | `SCR_CLASS_HUD_ELEM (1)` | HUD element |
| `"entity:pathnode"` | `SCR_CLASS_PATH_NODE (2)` | AI path node |
| `"entity:vehiclenode"` | `SCR_CLASS_VEHICLE_NODE (3)` | Vehicle path node |
| `"entity:dynentity"` | `SCR_CLASS_DYN_ENTITY (4)` | Dynamic entity (destructibles, etc.) |
| `"weapon"` | `SCR_CLASS_WEAPON (5)` | Weapon (mapped to `"weapon"`, not `"entity:weapon"`) |

### called_on (Methods)

For methods (`kind: "method"`), `called_on` indicates what the method is invoked on:

- **`"entity"`** — generic entity method (most common; handler calls `GetEntity()`)
- **`"entity:pathnode"`** — pathnode method (handler checks `entref.classnum == SCR_CLASS_PATH_NODE`)
- **`"entity:hudelem"`** — HUD element method
- **`"entity:vehiclenode"`** — vehicle node method
- **`"entity:dynentity"`** — dynamic entity method
- **`"weapon"`** — weapon method
- **`null`** — could not determine what it's called on. The handler takes `scr_entref_t` (confirmed method) but doesn't check classnum. These are still methods, we just don't know the target class.

---

## ScrVarType Enum (Reference)

These are the runtime type tag values used internally. They appear in `Scr_GetType` / `Scr_GetPointerType` comparisons:

```
SCR_TYPE_UNDEFINED       = 0x00
SCR_TYPE_POINTER         = 0x01
SCR_TYPE_STRING          = 0x02
SCR_TYPE_LOCALIZED_STRING= 0x03
SCR_TYPE_VECTOR          = 0x04
SCR_TYPE_HASH            = 0x05
SCR_TYPE_FLOAT           = 0x06
SCR_TYPE_INT             = 0x07
SCR_TYPE_UINT64          = 0x08
SCR_TYPE_ANIMATION       = 0x10
SCR_TYPE_SHARED_STRUCT   = 0x16
SCR_TYPE_STRUCT          = 0x17
SCR_TYPE_ENTITY          = 0x19
SCR_TYPE_ARRAY           = 0x1A
```

These values are specific to the binary being analysed and may differ across game builds.

---

## Known Limitations & Caveats

1. **Incomplete parameter lists.** If a parameter is passed through to another function without going through a recognised `Scr_Get*` / `BGScr_Get*` call, it won't appear. Some handlers use custom wrappers.

2. **"object" ambiguity.** `Scr_GetObject` / `Scr_AddObject` is used for arrays, structs, and other reference types. The extractor only refines these when an explicit `Scr_GetPointerType` / `Scr_GetType` check is nearby.

3. **Union completeness.** Union types are built from getters and type checks the extractor finds. If the handler accepts a type in a code path that doesn't use a recognisable getter or type check, that type will be missing from the union.

4. **Return type "void" ambiguity.** `"void"` means no `Scr_Add*` call was detected. It could mean the handler returns via a path the extractor didn't follow, or uses an unrecognised adder.

5. **Bool heuristic.** The `Scr_GetInt(inst, N) != 0` → bool promotion is a pattern match. Edge cases exist where this comparison is part of a larger arithmetic expression rather than a boolean coercion.

6. **Number vs float.** `"number"` (from `Scr_GetFloat`) means the parameter accepts both int and float input. Return types still distinguish `"int"` and `"float"` because `Scr_AddInt` and `Scr_AddFloat` are distinct operations that push specific types.

7. **Method detection.** Based on whether the handler's 2nd parameter type contains "entref" in the decompiler output. If IDA's type recovery fails, a method might be classified as a function or vice versa.

8. **called_on detection.** Many methods serve multiple entity types and only check classnum in error paths or not at all. A `null` called_on doesn't mean "called on nothing" — it means "we couldn't determine the target".

9. **min_args / max_args.** Extracted from the registration table init code via instruction heuristics. If the compiler reorders or optimises the stores differently, these may be wrong or null.

10. **error_strings.** Extracted via regex on pseudocode. May include format strings with `%s` placeholders. May miss errors in deeply nested helper functions.

11. **Registration table scope.** The extractor follows xrefs to `SL_GenerateCanonicalString`. Entries without a handler address are filtered out (these are field names, notify strings, etc., not functions). If the binary has multiple registration init functions, they should all be picked up as long as they call the same canonical string generator.

12. **Scr_AddValue.** Some handlers build a `ScrVarValue_t` struct manually and call `Scr_AddValue` rather than using a typed adder. These produce `"any"` as the return type. The actual type depends on what was put in the value struct (sometimes discernible from context like `ScrVar_AllocArray` → array, but not tracked by the extractor).

---

## Examples

### Plain Function

```json
{
  "name": "spawnfx",
  "kind": "function",
  "min_args": 2,
  "max_args": 4,
  "handler": "Scr_SpawnFX",
  "handler_addr": "0x1402ab3c0",
  "parameters": [
    { "index": 0, "type": "string" },
    { "index": 1, "type": "vector" },
    { "index": 2, "type": "vector" },
    { "index": 3, "type": "vector" }
  ],
  "return_type": "entity",
  "return_types": ["entity"],
  "return_subtypes": ["entity"],
  "overload_param_counts": [3, 4],
  "error_strings": [
    "spawnFx called with (0 0 0) forward direction",
    "spawnFx called with (0 0 0) up direction"
  ]
}
```

### Polymorphic Parameter Function

```json
{
  "name": "getdvarint",
  "kind": "function",
  "min_args": 1,
  "max_args": 2,
  "handler": "BGScr_GetDvarInt",
  "handler_addr": "0x140456789",
  "parameters": [
    { "index": 0, "type": "hash | int | string" },
    { "index": 1, "type": "int" }
  ],
  "return_type": "int",
  "return_types": ["int"],
  "return_subtypes": [],
  "overload_param_counts": [2],
  "error_strings": ["Optional argument must be a integer type"]
}
```

### Method with Entity Subtype

```json
{
  "name": "setdangerousnode",
  "kind": "method",
  "min_args": 2,
  "max_args": 2,
  "handler": "ScrCmd_SetDangerousNode",
  "handler_addr": "0x140123456",
  "called_on": "entity:pathnode",
  "parameters": [
    { "index": 0, "type": "team" },
    { "index": 1, "type": "bool" }
  ],
  "return_type": "void",
  "return_types": [],
  "return_subtypes": [],
  "overload_param_counts": [],
  "error_strings": ["SetDangerous not called on pathnode"]
}
```

### Overloaded Method with Multiple Return Types

```json
{
  "name": "spawnfromspawner",
  "kind": "method",
  "min_args": 0,
  "max_args": 6,
  "handler": "GScr_SpawnFromSpawner",
  "handler_addr": "0x140789abc",
  "called_on": "entity",
  "parameters": [
    { "index": 0, "type": "string" },
    { "index": 1, "type": "bool" },
    { "index": 2, "type": "bool" },
    { "index": 3, "type": "bool" },
    { "index": 4, "type": "string" },
    { "index": 5, "type": "bool" }
  ],
  "return_type": "entity",
  "return_types": ["entity"],
  "return_subtypes": ["entity"],
  "overload_param_counts": [2, 3, 4, 5, 6],
  "error_strings": [
    "SpawnFromSpawner can only be called on actor or vehicle spawners"
  ]
}
```

### Function with Number Parameter

```json
{
  "name": "arraysort",
  "kind": "function",
  "min_args": 2,
  "max_args": 5,
  "handler": "Scr_ArraySort",
  "handler_addr": "0x140aabbcc",
  "parameters": [
    { "index": 0, "type": "array" },
    { "index": 1, "type": "vector" },
    { "index": 2, "type": "bool" },
    { "index": 3, "type": "int" },
    { "index": 4, "type": "number" }
  ],
  "return_type": "void",
  "return_types": [],
  "return_subtypes": [],
  "overload_param_counts": [3, 4, 5],
  "error_strings": ["Parameter (%s) must be an array"]
}
```