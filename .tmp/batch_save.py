#!/usr/bin/env python3
"""Batch save remaining autoprocessed entries."""
import json, subprocess, sys

def save(name, entry, confidence, file='gsc'):
    proc = subprocess.run(
        [sys.executable, '.tmp/api_editor.py', 'save', name, confidence, '--file', file],
        input=json.dumps(entry), capture_output=True, text=True
    )
    print(proc.stderr.strip())

def stat_path_entry(name, desc, called_on, ret_type=None, ret_info=None):
    ordinals = ["first","second","third","fourth","fifth","sixth","seventh","eighth"]
    params = []
    for i in range(8):
        params.append({
            "name": f"statPath{i+1}",
            "description": f"The {ordinals[i]} level of the stat path.",
            "mandatory": i == 0,
            "type": {"dataType": "string", "isArray": False}
        })
    ov = {"parameters": params}
    if called_on:
        ov["calledOn"] = {"name": "player", "description": "The player.", "type": {"dataType": "entity", "instanceType": "player", "subType": "player", "isArray": False}}
    else:
        ov["calledOn"] = None
    if ret_type:
        ov["returns"] = {"name": ret_info[0], "description": ret_info[1], "type": {"dataType": ret_type, "isArray": False}, "void": False}
    else:
        ov["returns"] = {"void": True}
    return {"name": name, "description": desc, "overloads": [ov], "flags": ["autoprocessed"], "example": ""}

# Session stats
save("AddSessStat", stat_path_entry("AddSessStat", "Adds a session stat for the player.", True), "low")
save("GetSessStat", stat_path_entry("GetSessStat", "Gets a session stat for the player.", True, "int", ("value", "The stat value.")), "low")
save("GetSessStatArrayCount", stat_path_entry("GetSessStatArrayCount", "Gets the array count of a session stat for the player.", True, "int", ("count", "The array count.")), "low")

# Host migration
save("GetHostMigrationArrayCount", stat_path_entry("GetHostMigrationArrayCount", "Gets the array count of a host migration value.", False, "int", ("count", "The array count.")), "low")
save("GetHostMigrationValue", stat_path_entry("GetHostMigrationValue", "Gets a host migration value.", False), "low")
save("SetHostMigrationValue", stat_path_entry("SetHostMigrationValue", "Sets a host migration value.", False), "low")

# Match record loggers
save("matchRecordLogAdditionalDeathInfo", {
    "name": "MatchRecordLogAdditionalDeathInfo",
    "description": "Logs additional death information to the match record.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "attacker", "description": "The attacking entity.", "mandatory": True},
        {"name": "victim", "description": "The victim entity.", "mandatory": True},
        {"name": "weapon", "description": "The weapon used.", "mandatory": True},
        {"name": "meansOfDeath", "description": "The means of death.", "mandatory": True},
        {"name": "damage", "description": "The damage amount.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "isHeadshot", "description": "Whether the kill was a headshot.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "isLongshot", "description": "Whether the kill was a longshot.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "hitLocation", "description": "The hit location index.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "isRevenge", "description": "Whether the kill was a revenge.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "isBackstab", "description": "Whether the kill was a backstab.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "isMelee", "description": "Whether the kill was melee.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "isWallbang", "description": "Whether the kill was a wallbang.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "isHipfire", "description": "Whether the kill was from hipfire.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "isADS", "description": "Whether the kill was from ADS.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "extra1", "description": "Additional parameter.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
        {"name": "extra2", "description": "Additional parameter.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
        {"name": "extra3", "description": "Additional parameter.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "MatchRecordLogAdditionalDeathInfo( attacker, victim, weapon, mod, 100, true, false, 0, false, false, false, false, false, false );"
}, "low")

save("matchRecordLogSpecialMoveDataForLife", {
    "name": "MatchRecordLogSpecialMoveDataForLife",
    "description": "Logs special movement data for the current life in the match record.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "player", "description": "The player entity.", "mandatory": True},
        {"name": "wallRunCount", "description": "Number of wall runs.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "slideCount", "description": "Number of slides.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "doubleJumpCount", "description": "Number of double jumps.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "thrustJumpCount", "description": "Number of thrust jumps.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "swimCount", "description": "Number of swims.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "sprintCount", "description": "Number of sprints.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "mantleCount", "description": "Number of mantles.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "groundPoundCount", "description": "Number of ground pounds.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "extra1", "description": "Additional movement counter.", "mandatory": False},
        {"name": "extra2", "description": "Additional movement counter.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
        {"name": "extra3", "description": "Additional movement counter.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
        {"name": "extra4", "description": "Additional movement counter.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
        {"name": "extra5", "description": "Additional movement counter.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "MatchRecordLogSpecialMoveDataForLife( player, wallRuns, slides, doubleJumps, thrustJumps, swims, sprints, mantles, groundPounds );"
}, "low")

save("TrackWeaponFireNative", {
    "name": "TrackWeaponFireNative",
    "description": "Tracks a weapon fire event natively for the player.",
    "overloads": [{"calledOn": {"name": "player", "description": "The player.", "type": {"dataType": "entity", "instanceType": "player", "subType": "player", "isArray": False}}, "parameters": [
        {"name": "weapon", "description": "The weapon fired.", "mandatory": True},
        {"name": "shotsFired", "description": "Number of shots fired.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "shotsHit", "description": "Number of shots that hit.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "shotsMissed", "description": "Number of shots missed.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "damage", "description": "Total damage dealt.", "mandatory": True, "type": {"dataType": "int", "isArray": False}},
        {"name": "isADS", "description": "Whether the player was aiming down sights.", "mandatory": True, "type": {"dataType": "bool", "isArray": False}},
        {"name": "extra1", "description": "Additional parameter.", "mandatory": False},
        {"name": "extra2", "description": "Additional parameter.", "mandatory": False},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "self TrackWeaponFireNative( weapon, 1, 1, 0, 30, true );"
}, "low")

save("UpdateStatRatio", {
    "name": "UpdateStatRatio",
    "description": "Updates a stat ratio for the player.",
    "overloads": [{"calledOn": {"name": "player", "description": "The player.", "type": {"dataType": "entity", "instanceType": "player", "subType": "player", "isArray": False}}, "parameters": [
        {"name": "ratioName", "description": "The name of the ratio stat.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "numeratorStat", "description": "The numerator stat name.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "denominatorStat", "description": "The denominator stat name.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "self UpdateStatRatio( \"kd_ratio\", \"kills\", \"deaths\" );"
}, "low")

save("RatRecordMessage", {
    "name": "RatRecordMessage",
    "description": "Records a RAT (Remote Automated Testing) message.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "category", "description": "The message category.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "message", "description": "The message text.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "detail", "description": "Additional detail.", "mandatory": False, "type": {"dataType": "string", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "RatRecordMessage( \"test\", \"passed\" );"
}, "low")

save("RatReportCommandResult", {
    "name": "RatReportCommandResult",
    "description": "Reports a RAT command result.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "command", "description": "The command name.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "result", "description": "The command result.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "detail", "description": "Additional detail.", "mandatory": False, "type": {"dataType": "string", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "RatReportCommandResult( \"cmd\", \"success\" );"
}, "low")

save("linelist", {
    "name": "LineList",
    "description": "Draws a list of debug lines.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "param1", "description": "The first parameter.", "mandatory": True},
        {"name": "param2", "description": "The second parameter.", "mandatory": False},
        {"name": "param3", "description": "The third parameter.", "mandatory": False},
        {"name": "param4", "description": "The fourth parameter.", "mandatory": False},
        {"name": "param5", "description": "The fifth parameter.", "mandatory": False},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "LineList( data );"
}, "low")

save("SphericalCone", {
    "name": "SphericalCone",
    "description": "Draws a debug spherical cone.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "origin", "description": "The cone origin.", "mandatory": True, "type": {"dataType": "vector", "isArray": False}},
        {"name": "direction", "description": "The cone direction.", "mandatory": True, "type": {"dataType": "vector", "isArray": False}},
        {"name": "angle", "description": "The cone angle.", "mandatory": True, "type": {"dataType": "float", "isArray": False}},
        {"name": "radius", "description": "The cone radius.", "mandatory": False, "type": {"dataType": "float", "isArray": False}},
        {"name": "color", "description": "The color.", "mandatory": False, "type": {"dataType": "vector", "isArray": False}},
        {"name": "depthTest", "description": "Whether to depth test.", "mandatory": False, "type": {"dataType": "bool", "isArray": False}},
        {"name": "duration", "description": "The display duration.", "mandatory": False, "type": {"dataType": "float", "isArray": False}},
        {"name": "segments", "description": "The number of segments.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "SphericalCone( origin, dir, 45.0 );"
}, "low")

save("Vehicleclass", {
    "name": "VehicleClass",
    "description": "Defines or queries a vehicle class.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "className", "description": "The vehicle class name.", "mandatory": True, "type": {"dataType": "string", "isArray": False}},
        {"name": "param1", "description": "The first class parameter.", "mandatory": True},
        {"name": "param2", "description": "The second class parameter.", "mandatory": False},
        {"name": "param3", "description": "The third class parameter.", "mandatory": False},
        {"name": "param4", "description": "The fourth class parameter.", "mandatory": False},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "VehicleClass( \"helicopter\", data );"
}, "low")

save("Script_owner", {
    "name": "Script_Owner",
    "description": "Sets the script owner properties.",
    "overloads": [{"calledOn": None, "parameters": [
        {"name": "param1", "description": "The first parameter.", "mandatory": True},
        {"name": "param2", "description": "The second parameter.", "mandatory": True},
        {"name": "param3", "description": "The third parameter.", "mandatory": True},
        {"name": "param4", "description": "The fourth parameter.", "mandatory": True},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "Script_Owner( a, b, c, d );"
}, "low")

save("RecordLoadoutPerksAndKillStreaks", {
    "name": "RecordLoadoutPerksAndKillStreaks",
    "description": "Records the loadout perks and killstreaks for the player.",
    "overloads": [{"calledOn": {"name": "player", "description": "The player.", "type": {"dataType": "entity", "instanceType": "player", "subType": "player", "isArray": False}}, "parameters": [
        {"name": "perk1", "description": "The first perk.", "mandatory": False},
        {"name": "perk2", "description": "The second perk.", "mandatory": False},
        {"name": "perk3", "description": "The third perk.", "mandatory": False},
        {"name": "wildcard", "description": "The wildcard.", "mandatory": False},
        {"name": "killstreak1", "description": "The first killstreak index.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
        {"name": "killstreak2", "description": "The second killstreak index.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
        {"name": "killstreak3", "description": "The third killstreak index.", "mandatory": False, "type": {"dataType": "int", "isArray": False}},
    ], "returns": {"void": True}}],
    "flags": ["autoprocessed"],
    "example": "self RecordLoadoutPerksAndKillStreaks( perk1, perk2, perk3, wc, ks1, ks2, ks3 );"
}, "low")
