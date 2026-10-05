using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // Generic JSON -> object field mapper. Walks a JObject and copies
    // each property onto the matching public field (case-insensitive)
    // of the target, recursing into nested objects and lists. Unity
    // object references (prefabs, Resources, Sprites, ...) are looked
    // up by asset name, so JSON can say "damageType": "Resource White"
    // or "projectilePrefab": "Projectile Dart".
    public static class JsonFieldMapper
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        // A key that exists one level DOWN, inside a nested block, is the
        // likeliest thing an unmatched name was meant to be - and saying where
        // it lives turns "skipped" into a fix. Written for `range`: on a
        // hitscan weapon it is a real top-level field, on a projectile weapon
        // it is `rangeData.range`, and four of my own bloom presets put it at
        // the top level of a Popper clone. Every one of them silently kept
        // the Popper's reach of 15 while the file said 11, 18, 20 or 22.
        private static string NestedHint(Type type, string name)
        {
            try
            {
                var found = new List<string>();

                foreach (FieldInfo f in type.GetFields(
                             BindingFlags.Public | BindingFlags.Instance))
                {
                    Type ft = f.FieldType;

                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) ||
                        typeof(UnityEngine.Object).IsAssignableFrom(ft))
                    {
                        continue;
                    }

                    if (FindField(ft, name) != null)
                        found.Add(f.Name + "." + name);
                }

                if (found.Count == 0)
                    return "";

                return " (did you mean " +
                       string.Join(" or ", found.Select(s => "\"" + s + "\"").ToArray()) +
                       "? On this weapon type it lives inside that block)";
            }
            catch (Exception)
            {
                // A hint must never cost the warning it is attached to.
                return "";
            }
        }

        // Applies every property in json onto target. Returns the
        // (possibly re-boxed) target so struct targets keep changes.
        public static object Apply(
            object target,
            JObject json,
            string path)
        {
            if (target == null || json == null)
                return target;

            Type type = target.GetType();

            foreach (JProperty property in json.Properties())
            {
                FieldInfo field =
                    FindField(type, property.Name);

                if (field == null)
                {
                    Log.LogWarning(
                        path + "." + property.Name +
                        " does not match any field on " +
                        type.Name + " - skipped" +
                        NestedHint(type, property.Name));
                    continue;
                }

                try
                {
                    object converted;

                    if (TryConvert(
                        property.Value,
                        field.FieldType,
                        field.GetValue(target),
                        path + "." + property.Name,
                        out converted))
                    {
                        field.SetValue(target, converted);
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning(
                        path + "." + property.Name + ": " +
                        e.Message);
                }
            }

            return target;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            FieldInfo[] fields =
                type.GetFields(
                    BindingFlags.Public |
                    BindingFlags.Instance);

            foreach (FieldInfo field in fields)
            {
                if (string.Equals(
                    field.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }

            return null;
        }

        private static bool TryConvert(
            JToken token,
            Type fieldType,
            object currentValue,
            string path,
            out object result)
        {
            result = null;

            // Unity object references: look up by asset name.
            if (typeof(UnityEngine.Object)
                .IsAssignableFrom(fieldType))
            {
                if (token.Type == JTokenType.Null)
                {
                    result = null;
                    return true;
                }

                string assetName = token.ToString();

                // ColorAssets also accept raw hex colors, e.g.
                // "#7fd4ff" — a new ColorAsset is created on the fly.
                if (fieldType == typeof(ColorAsset) &&
                    assetName.StartsWith("#"))
                {
                    Color parsed;

                    if (!ColorUtility.TryParseHtmlString(
                        assetName,
                        out parsed))
                    {
                        Log.LogWarning(
                            path + ": '" + assetName +
                            "' is not a valid hex color");
                        return false;
                    }

                    var colorAsset =
                        ScriptableObject
                            .CreateInstance<ColorAsset>();

                    colorAsset.name =
                        "Forge Color " + assetName;

                    colorAsset.color = parsed;

                    result = colorAsset;
                    return true;
                }

                UnityEngine.Object found =
                    FindAsset(fieldType, assetName);

                if (found == null)
                {
                    Log.LogWarning(
                        path + ": no " + fieldType.Name +
                        " asset named '" + assetName +
                        "' was found" +
                        (fieldType == typeof(ColorAsset) ? ColorNames() : ""));
                    return false;
                }

                result = found;
                return true;
            }

            if (fieldType.IsEnum)
            {
                result = Enum.Parse(
                    fieldType,
                    token.ToString(),
                    true);
                return true;
            }

            // AnimationCurve as [[time, value], ...] - used by things like
            // homingData.turbulenceDistanceCurve, where the curve maps
            // distance-to-target onto how strong an effect is.
            if (fieldType == typeof(AnimationCurve))
            {
                if (token.Type != JTokenType.Array)
                    return false;

                var curve = new AnimationCurve();
                foreach (JToken point in token)
                {
                    if (point.Type != JTokenType.Array)
                        continue;
                    var pair = point.ToArray();
                    if (pair.Length >= 2)
                        curve.AddKey((float)pair[0], (float)pair[1]);
                }

                if (curve.length == 0)
                    return false;

                result = curve;
                return true;
            }

            if (fieldType == typeof(LayerMask))
            {
                if (token.Type == JTokenType.Integer)
                {
                    result = (LayerMask)(int)token;
                    return true;
                }

                if (token.Type == JTokenType.String)
                {
                    result = (LayerMask)LayerMask.GetMask(
                        token.ToString());
                    return true;
                }

                if (token.Type == JTokenType.Array)
                {
                    result = (LayerMask)LayerMask.GetMask(
                        token.Select(t => t.ToString())
                            .ToArray());
                    return true;
                }

                return false;
            }

            if (fieldType == typeof(float))
            {
                result = (float)token;
                return true;
            }

            if (fieldType == typeof(int))
            {
                result = (int)token;
                return true;
            }

            if (fieldType == typeof(bool))
            {
                result = (bool)token;
                return true;
            }

            if (fieldType == typeof(string))
            {
                result = token.ToString();
                return true;
            }

            // A single asset NAME where a list of assets is expected, e.g.
            // "convertableCells": "CellType_Mud" - treat it as a one-item
            // list so you don't have to write [...] for the common case.
            if (token.Type == JTokenType.String &&
                fieldType.IsGenericType &&
                fieldType.GetGenericTypeDefinition() == typeof(List<>) &&
                typeof(UnityEngine.Object).IsAssignableFrom(
                    fieldType.GetGenericArguments()[0]))
            {
                Type single = fieldType.GetGenericArguments()[0];
                UnityEngine.Object one = FindAsset(single, token.ToString());

                if (one == null)
                {
                    Log.LogWarning(
                        path + ": no " + single.Name + " asset named '" +
                        token + "' was found");
                    return false;
                }

                IList oneList = (IList)Activator.CreateInstance(fieldType);
                oneList.Add(one);
                result = oneList;
                return true;
            }

            // Lists of structs/classes, e.g. Explosion.damages.
            if (token.Type == JTokenType.Array &&
                fieldType.IsGenericType &&
                fieldType.GetGenericTypeDefinition() ==
                    typeof(List<>))
            {
                Type elementType =
                    fieldType.GetGenericArguments()[0];

                IList list =
                    (IList)Activator.CreateInstance(fieldType);

                // A list of ASSETS (e.g. cellConvertData.convertableCells,
                // a List<CellType>) is written as a list of asset NAMES -
                // those must be looked up, never constructed.
                if (typeof(UnityEngine.Object).IsAssignableFrom(elementType))
                {
                    foreach (JToken element in (JArray)token)
                    {
                        string assetName = element.ToString();
                        UnityEngine.Object asset =
                            FindAsset(elementType, assetName);

                        if (asset == null)
                        {
                            Log.LogWarning(
                                path + ": no " + elementType.Name +
                                " asset named '" + assetName + "' was found");
                            continue;
                        }

                        list.Add(asset);
                    }

                    result = list;
                    return true;
                }

                int index = 0;

                foreach (JToken element in (JArray)token)
                {
                    object item =
                        Activator.CreateInstance(elementType);

                    JObject elementObject = element as JObject;

                    if (elementObject != null)
                    {
                        item = Apply(
                            item,
                            elementObject,
                            path + "[" + index + "]");
                    }

                    list.Add(item);
                    index++;
                }

                result = list;
                return true;
            }

            // Nested serializable struct/class: recurse into the
            // field's current value so unspecified members keep
            // their template values.
            JObject nested = token as JObject;

            if (nested != null)
            {
                object value = currentValue;

                if (value == null && !fieldType.IsValueType)
                {
                    value =
                        Activator.CreateInstance(fieldType);
                }

                result = Apply(value, nested, path);
                return true;
            }

            // A BARE NUMBER FOR A FIELD THAT IS AN "AMOUNT PLUS A KIND" - the
            // game's `Damage` is `{ amount, damageType }`, and "damage": 14 is
            // what everybody types. My own R19_Tollgate.json did, and the weapon
            // shipped at the Popper's damage with "unsupported field type
            // Damage" in the log as the only sign. So a number fills `amount`
            // and the template keeps its type - which is the only thing the
            // number could have meant.
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                FieldInfo amount = fieldType.GetField(
                    "amount", BindingFlags.Public | BindingFlags.Instance);

                if (amount != null && amount.FieldType == typeof(float))
                {
                    object value = currentValue;

                    if (value == null && !fieldType.IsValueType)
                        value = Activator.CreateInstance(fieldType);

                    if (value != null)
                    {
                        amount.SetValue(value, (float)token);
                        result = value;
                        return true;
                    }
                }
            }

            Log.LogWarning(
                path + ": unsupported field type " + fieldType.Name +
                (token.Type == JTokenType.Object || token.Type == JTokenType.Array
                    ? ""
                    : " - this field is an OBJECT with fields of its own, so a " +
                      "single value cannot fill it (write it as { \"field\": " +
                      "value } - see the how-to for its fields)"));
            return false;
        }

        // ★ R20's log: "no ColorAsset asset named 'ColorGreen' was found" - a
        // name that looks right and does not exist (the game's green is
        // `ColorLevelUp`). A miss that names only the input sends the reader to
        // check their spelling; naming what DOES exist ends the search. The
        // game ships nine, so the whole list fits on the line.
        private static string ColorNames()
        {
            try
            {
                var names = new List<string>();

                foreach (ColorAsset c in Resources.FindObjectsOfTypeAll<ColorAsset>())
                {
                    if (c != null && !string.IsNullOrEmpty(c.name) &&
                        !c.name.StartsWith("Forge Color ") && !names.Contains(c.name))
                    {
                        names.Add(c.name);
                    }
                }

                if (names.Count == 0)
                    return ". Write a #hex colour instead, e.g. \"#6cff7a\".";

                names.Sort(StringComparer.OrdinalIgnoreCase);

                return ". The game's colours are " + string.Join(", ", names.ToArray()) +
                       " (its green is ColorLevelUp) - or write a #hex colour, " +
                       "e.g. \"#6cff7a\".";
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static UnityEngine.Object FindAsset(
            Type type,
            string name)
        {
            UnityEngine.Object[] all =
                Resources.FindObjectsOfTypeAll(type);

            foreach (UnityEngine.Object asset in all)
            {
                if (string.Equals(
                    asset.name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }

            // Convenience: allow "White" for "Resource White".
            if (type == typeof(Resource))
            {
                foreach (UnityEngine.Object asset in all)
                {
                    if (string.Equals(
                        asset.name,
                        "Resource " + name,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return asset;
                    }
                }
            }

            return null;
        }
    }
}
