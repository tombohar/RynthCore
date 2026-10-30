// ============================================================================
//  RynthCore.Engine - UI/Data/EnlRowConverters.cs
//  Lenient readers for the Enl calculator's row arrays (levels / materials / chain
//  in the Progression snapshot). The calculator is an add-on of the planner: a bad
//  field in it must not turn the whole Progression snapshot into "unavailable" (the
//  default for a JsonException), which would also hide the Augmentation planner and
//  the attribute raiser. So:
//    - null, or anything that is not an array, reads as an empty array;
//    - a row that is not an object is skipped;
//    - a field of the wrong type keeps its default, other fields of the row still read;
//    - unknown fields are ignored; at most MaxRows rows are kept (RynthILT sends 100 at most).
//  Read only (the engine never writes this JSON). No reflection: safe under trimming / NativeAOT.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Engine.UI.Data;

/// <summary>Reads a JSON array of row objects, tolerating whatever else shows up.</summary>
internal abstract class EnlRowsConverter<TRow> : JsonConverter<TRow[]> where TRow : class
{
    /// <summary>Rows kept per array; the producer's own cap is 100 (levels).</summary>
    internal const int MaxRows = 200;

    // A JSON null reaches Read (and becomes an empty array) instead of null.
    public override bool HandleNull => true;

    /// <summary>Reads one row. The reader is on its StartObject; leave it on the matching EndObject.</summary>
    protected abstract TRow ReadRow(ref Utf8JsonReader reader);

    public override TRow[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();   // a scalar stays as it is, an object is skipped whole
            return Array.Empty<TRow>();
        }

        var rows = new List<TRow>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }
            TRow row = ReadRow(ref reader);
            if (rows.Count < MaxRows) rows.Add(row);
        }
        return rows.Count == 0 ? Array.Empty<TRow>() : rows.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, TRow[] value, JsonSerializerOptions options)
        => throw new NotSupportedException("The Enl calculator rows are read-only.");

    // -- Field readers: the reader is on the value; each consumes exactly that value. --

    protected static int ReadInt(ref Utf8JsonReader reader, int fallback = 0)
    {
        long v = ReadLong(ref reader, fallback);
        return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
    }

    protected static long ReadLong(ref Utf8JsonReader reader, long fallback = 0)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long l)) return l;
                if (reader.TryGetDouble(out double d) && double.IsFinite(d))
                    return d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;
                return fallback;
            case JsonTokenType.String:
                return long.TryParse(reader.GetString(), NumberStyles.Integer | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out long s) ? s : fallback;
            default:
                reader.Skip();
                return fallback;
        }
    }

    protected static bool ReadBool(ref Utf8JsonReader reader, bool fallback = false)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            default:
                reader.Skip();
                return fallback;
        }
    }

    protected static string ReadString(ref Utf8JsonReader reader, string fallback = "")
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String: return reader.GetString() ?? fallback;
            case JsonTokenType.Number: return Encoding.UTF8.GetString(reader.ValueSpan);
            default:
                reader.Skip();
                return fallback;
        }
    }

    /// <summary>Moves to the next property name of the object being read; false at its EndObject.</summary>
    protected static bool NextProperty(ref Utf8JsonReader reader)
        => reader.Read() && reader.TokenType == JsonTokenType.PropertyName;
}

internal sealed class EnlLevelRowsConverter : EnlRowsConverter<EnlLevelRow>
{
    protected override EnlLevelRow ReadRow(ref Utf8JsonReader reader)
    {
        var row = new EnlLevelRow();
        while (NextProperty(ref reader))
        {
            if (reader.ValueTextEquals("t")) { reader.Read(); row.Level = ReadInt(ref reader); }
            else if (reader.ValueTextEquals("item")) { reader.Read(); row.Item = ReadString(ref reader, "-"); }
            else if (reader.ValueTextEquals("count")) { reader.Read(); row.Count = ReadInt(ref reader); }
            else if (reader.ValueTextEquals("lum")) { reader.Read(); row.Lum = ReadString(ref reader, "-"); }
            else if (reader.ValueTextEquals("ok")) { reader.Read(); row.Ok = ReadBool(ref reader); }
            else { reader.Read(); reader.Skip(); }
        }
        return row;
    }
}

internal sealed class EnlMaterialRowsConverter : EnlRowsConverter<EnlMaterialRow>
{
    protected override EnlMaterialRow ReadRow(ref Utf8JsonReader reader)
    {
        var row = new EnlMaterialRow();
        while (NextProperty(ref reader))
        {
            if (reader.ValueTextEquals("label")) { reader.Read(); row.Label = ReadString(ref reader); }
            else if (reader.ValueTextEquals("need")) { reader.Read(); row.Need = ReadString(ref reader, "0"); }
            else if (reader.ValueTextEquals("have")) { reader.Read(); row.Have = ReadString(ref reader, "0"); }
            else if (reader.ValueTextEquals("ok")) { reader.Read(); row.Ok = ReadBool(ref reader); }
            else { reader.Read(); reader.Skip(); }
        }
        return row;
    }
}

internal sealed class EnlChainRowsConverter : EnlRowsConverter<EnlChainRow>
{
    protected override EnlChainRow ReadRow(ref Utf8JsonReader reader)
    {
        var row = new EnlChainRow();
        while (NextProperty(ref reader))
        {
            if (reader.ValueTextEquals("label")) { reader.Read(); row.Label = ReadString(ref reader); }
            else if (reader.ValueTextEquals("need")) { reader.Read(); row.Need = ReadLong(ref reader); }
            else if (reader.ValueTextEquals("have")) { reader.Read(); row.Have = ReadLong(ref reader); }
            else if (reader.ValueTextEquals("ok")) { reader.Read(); row.Ok = ReadBool(ref reader); }
            else if (reader.ValueTextEquals("craft")) { reader.Read(); row.Craft = ReadBool(ref reader); }
            else { reader.Read(); reader.Skip(); }
        }
        return row;
    }
}
