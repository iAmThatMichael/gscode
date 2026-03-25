#!/usr/bin/env python3
"""Batch save CSC autoprocessed entries."""
import json, subprocess, sys

def save(name, entry, confidence, file='csc'):
    proc = subprocess.run(
        [sys.executable, '.tmp/api_editor.py', 'save', name, confidence, '--file', file],
        input=json.dumps(entry), capture_output=True, text=True
    )
    print(proc.stderr.strip())

save("AnimGetChildAt", {
    "name": "AnimGetChildAt",
    "description": "Gets the child animation name at the given index.",
    "overloads": [{"calledOn": {"name": "entity", "description": "The entity to query.", "type": {"dataType": "entity", "isArray": False}}, "parameters": [
        {"name": "parentAnim", "description": "The parent animation name.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "childIndex", "description": "The child animation index.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
    ], "returns": {"name": "childAnim", "description": "The child animation name.", "type": {"dataType": "string", "isArray": False}, "void": False}}],
    "flags": ["autoprocessed"],
    "example": "childAnim = self AnimGetChildAt( \"idle\", 0 );"
}, "high")

save("AnimGetNumChildren", {
    "name": "AnimGetNumChildren",
    "description": "Gets the number of child animations for the given animation.",
    "overloads": [{"calledOn": {"name": "entity", "description": "The entity to query.", "type": {"dataType": "entity", "isArray": False}}, "parameters": [
        {"name": "animName", "description": "The animation name.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
    ], "returns": {"name": "numChildren", "description": "The number of child animations.", "type": {"dataType": "int", "isArray": False}, "void": False}}],
    "flags": ["autoprocessed"],
    "example": "count = self AnimGetNumChildren( \"idle\" );"
}, "high")

save("CloseFile", {
    "name": "CloseFile",
    "description": "Closes an open file.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "fileHandle", "description": "The file handle to close.", "mandatory": True},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "CloseFile( handle );"
}, "medium")

save("Debugbreak", {
    "name": "DebugBreak",
    "description": "Triggers a debug breakpoint in the script.",
    "overloads": [{"calledOn": None, "parameters": [], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "DebugBreak();"
}, "high")

save("FGetArg", {
    "name": "FGetArg",
    "description": "Gets an argument from the last read file line.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "fileHandle", "description": "The file handle.", "mandatory": True},
        {"name": "argIndex", "description": "The argument index.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "arg = FGetArg( handle, 0 );"
}, "medium")

save("FPrintFields", {
    "name": "FPrintFields",
    "description": "Prints field data to an open file.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "fileHandle", "description": "The file handle to write to.", "mandatory": True},
        {"name": "data", "description": "The field data to write.", "mandatory": True},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "FPrintFields( handle, data );"
}, "low")

save("FPrintln", {
    "name": "FPrintln",
    "description": "Prints a line to an open file.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "fileHandle", "description": "The file handle to write to.", "mandatory": True},
        {"name": "text", "description": "The text to write.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "FPrintln( handle, \"data\" );"
}, "medium")

save("FReadLn", {
    "name": "FReadLn",
    "description": "Reads a line from an open file.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "fileHandle", "description": "The file handle to read from.", "mandatory": True},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "FReadLn( handle );"
}, "medium")

save("IsCybercomIndexEnabled", {
    "name": "IsCybercomIndexEnabled",
    "description": "Checks whether a cybercom ability at the given index is enabled for the player.",
    "overloads": [{"calledOn": {"name": "player", "description": "The player.", "type": {"dataType": "entity", "instanceType": "player", "subType": "player", "isArray": False}}, "parameters": [
        {"name": "cybercomIndex", "description": "The cybercom ability index.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "slotIndex", "description": "The slot index.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
    ], "returns": {"name": "enabled", "description": "Whether the cybercom ability is enabled.", "type": {"dataType": "bool", "isArray": False}, "void": False}}],
    "flags": ["autoprocessed"],
    "example": "if ( self IsCybercomIndexEnabled( 0, 0 ) )"
}, "medium")

save("IsProfileBuild", {
    "name": "IsProfileBuild",
    "description": "Checks whether the current build is a profile build.",
    "overloads": [{"calledOn": None, "parameters": [], "returns": {"name": "isProfileBuild", "description": "Whether this is a profile build.", "type": {"dataType": "bool", "isArray": False}, "void": False}}],
    "flags": ["autoprocessed"],
    "example": "if ( IsProfileBuild() )"
}, "high")

save("OpenFile", {
    "name": "OpenFile",
    "description": "Opens a file for reading or writing.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "fileName", "description": "The name of the file to open.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "mode", "description": "The file open mode.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "OpenFile( \"data.txt\", \"read\" );"
}, "medium")

save("SetAllControllersLightbarColor", {
    "name": "SetAllControllersLightbarColor",
    "description": "Sets the lightbar color on all connected controllers.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "color", "description": "The RGB color vector.", "mandatory": False, "type": {"dataType": "vector", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "SetAllControllersLightbarColor( (1, 0, 0) );"
}, "high")

save("SetPlayerCybercomAbility", {
    "name": "SetPlayerCybercomAbility",
    "description": "Sets the cybercom ability for the player.",
    "overloads": [{"calledOn": {"name": "player", "description": "The player.", "type": {"dataType": "entity", "instanceType": "player", "subType": "player", "isArray": False}}, "parameters": [
        {"name": "abilityName", "description": "The name of the cybercom ability.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "self SetPlayerCybercomAbility( \"psychosis\" );"
}, "medium")

save("useAlternateReviveIcon", {
    "name": "UseAlternateReviveIcon",
    "description": "Sets whether to use the alternate revive icon.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "useAlternate", "description": "Whether to use the alternate icon.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "UseAlternateReviveIcon( true );"
}, "medium")
