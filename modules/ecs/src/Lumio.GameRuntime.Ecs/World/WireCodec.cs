using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.GameRuntime.Ecs;

/// <summary>Single C-1 gameplay-envelope codec shared by authoritative and replica worlds.</summary>
public static class WireCodec
{
    public const string ChatInput = "chat.input";
    public const string FieldWrite = "field.write";
    public const string ServerRpc = "server.rpc";

    public static byte[] EncodePack(WorldMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        var json = new StringBuilder(256);
        if (message is WelcomeMessage welcome)
        {
            json.Append("{\"connectionGeneration\":").Append(welcome.ConnectionGeneration.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"instanceId\":").Append(welcome.InstanceId.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"messageType\":\"Welcome\",\"selfNetEntityId\":");
            AppendString(json, welcome.Self.ToHex());
            json.Append('}');
            return Encoding.UTF8.GetBytes(json.ToString());
        }

        if (message is WorldChangeMessage change)
        {
            json.Append("{\"creates\":[");
            for (int i = 0; i < change.Creates.Count; i++)
            {
                if (i > 0) json.Append(',');
                CreateRecord create = change.Creates[i];
                json.Append("{\"entityType\":"); AppendString(json, create.EntityType);
                json.Append(",\"fields\":[");
                for (int f = 0; f < create.Fields.Count; f++)
                {
                    if (f > 0) json.Append(',');
                    FieldValue field = create.Fields[f];
                    json.Append("{\"componentId\":"); AppendString(json, field.ComponentId);
                    json.Append(",\"fieldId\":"); AppendString(json, field.FieldId);
                    json.Append(",\"value\":"); AppendString(json, ValueText(field.Value));
                    json.Append('}');
                }
                json.Append("] ,\"netEntityId\":"); AppendString(json, create.NetEntityId.ToHex());
                json.Append('}');
            }
            json.Append("],\"destroys\":[");
            for (int i = 0; i < change.Destroys.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"netEntityId\":"); AppendString(json, change.Destroys[i].ToHex()); json.Append('}');
            }
            json.Append("],\"fields\":[");
            for (int i = 0; i < change.Fields.Count; i++)
            {
                if (i > 0) json.Append(',');
                FieldChange field = change.Fields[i];
                json.Append("{\"componentId\":"); AppendString(json, field.ComponentId);
                json.Append(",\"fieldId\":"); AppendString(json, field.FieldId);
                json.Append(",\"netEntityId\":"); AppendString(json, field.NetEntityId.ToHex());
                json.Append(",\"reason\":"); AppendString(json, field.Reason == ChangeReason.Correction ? "correction" : "sync");
                json.Append(",\"value\":"); AppendString(json, ValueText(field.Value)); json.Append('}');
            }
            json.Append("],\"messageType\":\"WorldChange\",\"rpcs\":[");
            for (int i = 0; i < change.Rpcs.Count; i++)
            {
                if (i > 0) json.Append(',');
                ClientRpcRecord rpc = change.Rpcs[i];
                json.Append("{\"appliedTick\":").Append(rpc.AppliedTick.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"args\":"); AppendString(json, ToHex(Encoding.UTF8.GetBytes(rpc.Args.Count == 0 ? string.Empty : rpc.Args[0]?.ToString() ?? string.Empty)));
                json.Append(",\"componentId\":"); AppendString(json, rpc.ComponentId);
                json.Append(",\"messageId\":").Append(rpc.MessageId.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"method\":"); AppendString(json, rpc.Method);
                json.Append(",\"roomSequence\":").Append(rpc.RoomSequence.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"sender\":"); AppendString(json, rpc.Sender.ToHex());
                json.Append(",\"target\":"); AppendString(json, rpc.Target.ToHex()); json.Append('}');
            }
            json.Append("],\"tick\":").Append(change.Tick.ToString(CultureInfo.InvariantCulture)).Append('}');
            return Encoding.UTF8.GetBytes(json.ToString().Replace("] ,", "],", StringComparison.Ordinal));
        }

        if (message is InputCommandMessage input) return EncodeInput(input);
        if (message is ConnectionSupersededMessage superseded)
        {
            json.Append("{\"messageType\":\"ConnectionSuperseded\",\"netEntityId\":"); AppendString(json, superseded.NetEntityId.ToHex());
            json.Append(",\"newConnectionGeneration\":").Append(superseded.NewConnectionGeneration.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"reasonCode\":\"connection_superseded\"}");
            return Encoding.UTF8.GetBytes(json.ToString());
        }
        if (message is ErrorMessage error)
        {
            json.Append("{\"code\":"); AppendString(json, error.Code); json.Append(",\"detail\":"); AppendString(json, error.Detail); json.Append(",\"messageType\":\"Error\"}");
            return Encoding.UTF8.GetBytes(json.ToString());
        }
        throw new ArgumentException("Unsupported C-1 message type.", nameof(message));
    }

    public static WorldMessage DecodePack(ReadOnlySpan<byte> bytes)
    {
        JsonValue root = JsonValue.Parse(Encoding.UTF8.GetString(bytes));
        string type = root.RequiredString("messageType");
        if (type == "Welcome")
        {
            if (!NetEntityId.TryParse(root.RequiredString("selfNetEntityId"), out NetEntityId self)) throw new FormatException("selfNetEntityId must be 128-bit hex.");
            return new WelcomeMessage(root.RequiredU64("instanceId"), self, root.RequiredU64("connectionGeneration"));
        }
        if (type == "WorldChange") return DecodeWorldChange(root);
        if (type == "InputCommand") return DecodeInput(bytes);
        if (type == "ConnectionSuperseded") return new ConnectionSupersededMessage(NetEntityId.Parse(root.RequiredString("netEntityId")), root.RequiredU64("newConnectionGeneration"));
        if (type == "Error") return new ErrorMessage(root.RequiredString("code"), root.RequiredString("detail"));
        throw new FormatException("unknown messageType: " + type);
    }

    public static byte[] EncodeInput(InputCommandMessage input)
    {
        byte[] payload = input.Payload.ToArray();
        string hash = Sha256(payload);
        var json = new StringBuilder("{\"commands\":[{\"mappingId\":");
        AppendString(json, input.MappingId);
        json.Append(",\"payload\":"); AppendString(json, ToHex(payload));
        json.Append(",\"payloadSha256\":"); AppendString(json, hash);
        json.Append("}],\"messageType\":\"InputCommand\"}");
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    public static InputCommandMessage DecodeInput(ReadOnlySpan<byte> bytes)
        => DecodeInput(bytes, default);

    /// <summary>Decodes an input envelope while the host supplies the authenticated sender identity.</summary>
    public static InputCommandMessage DecodeInput(ReadOnlySpan<byte> bytes, NetEntityId sender)
    {
        JsonValue root = JsonValue.Parse(Encoding.UTF8.GetString(bytes));
        if (root.RequiredString("messageType") != "InputCommand") throw new FormatException("messageType must be InputCommand.");
        List<JsonValue> commands = root.RequiredArray("commands");
        if (commands.Count != 1) throw new FormatException("exactly one command is required");
        JsonValue command = commands[0];
        string mapping = command.RequiredString("mappingId");
        if (mapping != ChatInput && mapping != FieldWrite && mapping != ServerRpc) throw new FormatException("unknown command mapping.");
        byte[] payload = ParseHex(command.RequiredString("payload"));
        if (!string.Equals(Sha256(payload), command.RequiredString("payloadSha256"), StringComparison.Ordinal)) throw new FormatException("bad_payload_hash");
        if (mapping == ChatInput && (!TryReadUtf8Payload(payload, out string text) || Encoding.UTF8.GetByteCount(text) > 512)) throw new FormatException("undecodable_payload");
        if (mapping == FieldWrite && !TryDecodeFieldWrite(payload, out _, out _, out _, out _)) throw new FormatException("undecodable_payload");
        if (mapping == ServerRpc && !TryDecodeServerRpc(payload, out _, out _, out _)) throw new FormatException("undecodable_payload");
        return new InputCommandMessage(mapping, sender, payload);
    }

    private static WorldChangeMessage DecodeWorldChange(JsonValue root)
    {
        var creates = new List<CreateRecord>();
        foreach (JsonValue item in root.RequiredArray("creates"))
        {
            var fields = new List<FieldValue>();
            foreach (JsonValue field in item.RequiredArray("fields"))
                fields.Add(new FieldValue(field.RequiredString("componentId"), field.RequiredString("fieldId"), field.RequiredString("value")));
            creates.Add(new CreateRecord(item.RequiredString("entityType"), NetEntityId.Parse(item.RequiredString("netEntityId")), fields));
        }
        var changed = new List<FieldChange>();
        foreach (JsonValue field in root.RequiredArray("fields"))
        {
            string reason = field.RequiredString("reason");
            if (reason != "sync" && reason != "correction") throw new FormatException("unknown change reason");
            changed.Add(new FieldChange(NetEntityId.Parse(field.RequiredString("netEntityId")), field.RequiredString("componentId"), field.RequiredString("fieldId"), field.RequiredString("value"), reason == "correction" ? ChangeReason.Correction : ChangeReason.Sync));
        }
        var destroys = new List<NetEntityId>();
        foreach (JsonValue item in root.RequiredArray("destroys")) destroys.Add(NetEntityId.Parse(item.RequiredString("netEntityId")));
        var rpcs = new List<ClientRpcRecord>();
        foreach (JsonValue item in root.RequiredArray("rpcs"))
        {
            string argHex = item.RequiredString("args");
            string arg = Encoding.UTF8.GetString(ParseHex(argHex));
            rpcs.Add(new ClientRpcRecord(NetEntityId.Parse(item.RequiredString("target")), item.RequiredString("componentId"), item.RequiredString("method"), new object?[] { arg }, item.RequiredU64("messageId"), item.RequiredU64("roomSequence"), NetEntityId.Parse(item.RequiredString("sender")), item.RequiredU64("appliedTick")));
        }
        return new WorldChangeMessage(root.RequiredU64("tick"), creates, changed, destroys, rpcs);
    }

    internal static byte[] EncodeUtf8(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var bytes = new byte[4 + utf8.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, bytes, 4, utf8.Length);
        return bytes;
    }

    public static bool TryReadUtf8Payload(ReadOnlySpan<byte> payload, out string text)
    {
        text = string.Empty;
        if (payload.Length < 4) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (length != payload.Length - 4) return false;
        text = Encoding.UTF8.GetString(payload.Slice(4));
        return true;
    }

    internal static byte[] EncodeFieldWrite(NetEntityId target, string componentId, string fieldId, string value)
    {
        byte[] component = Encoding.UTF8.GetBytes(componentId ?? string.Empty);
        byte[] field = Encoding.UTF8.GetBytes(fieldId ?? string.Empty);
        byte[] text = Encoding.UTF8.GetBytes(value ?? string.Empty);
        byte[] result = new byte[16 + 4 + component.Length + 4 + field.Length + 4 + text.Length];
        int offset = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset, 8), target.InstanceId); offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset, 8), target.Counter); offset += 8;
        WriteBytes(result, ref offset, component); WriteBytes(result, ref offset, field); WriteBytes(result, ref offset, text);
        return result;
    }

    internal static bool TryDecodeFieldWrite(ReadOnlySpan<byte> payload, out NetEntityId target, out string componentId, out string fieldId, out string value)
    {
        target = default; componentId = fieldId = value = string.Empty;
        int offset = 0;
        if (!TryReadU64(payload, ref offset, out ulong instance) || !TryReadU64(payload, ref offset, out ulong counter) || !TryReadString(payload, ref offset, out componentId) || !TryReadString(payload, ref offset, out fieldId) || !TryReadString(payload, ref offset, out value)) return false;
        if (offset != payload.Length) return false;
        target = new NetEntityId(instance, counter); return true;
    }

    internal static byte[] EncodeServerRpc(string componentId, string method, string argument)
    {
        byte[] result = new byte[4 + Encoding.UTF8.GetByteCount(componentId) + 4 + Encoding.UTF8.GetByteCount(method) + 4 + Encoding.UTF8.GetByteCount(argument)];
        int offset = 0; WriteBytes(result, ref offset, Encoding.UTF8.GetBytes(componentId)); WriteBytes(result, ref offset, Encoding.UTF8.GetBytes(method)); WriteBytes(result, ref offset, Encoding.UTF8.GetBytes(argument)); return result;
    }

    internal static bool TryDecodeServerRpc(ReadOnlySpan<byte> payload, out string componentId, out string method, out string argument)
    {
        componentId = method = argument = string.Empty; int offset = 0;
        return TryReadString(payload, ref offset, out componentId) && TryReadString(payload, ref offset, out method) && TryReadString(payload, ref offset, out argument) && offset == payload.Length;
    }

    private static void WriteBytes(byte[] target, ref int offset, byte[] value) { BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), (uint)value.Length); offset += 4; Buffer.BlockCopy(value, 0, target, offset, value.Length); offset += value.Length; }
    private static bool TryReadU64(ReadOnlySpan<byte> payload, ref int offset, out ulong value) { value = 0; if (offset + 8 > payload.Length) return false; value = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8)); offset += 8; return true; }
    private static bool TryReadString(ReadOnlySpan<byte> payload, ref int offset, out string value) { value = string.Empty; if (offset + 4 > payload.Length) return false; uint len = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)); offset += 4; if (len > (uint)(payload.Length - offset)) return false; value = Encoding.UTF8.GetString(payload.Slice(offset, (int)len)); offset += (int)len; return true; }
    private static string ValueText(object? value)
    {
        if (value is SyncList<NetEntityId> list)
        {
            var ids = new StringBuilder();
            for (int i = 0; i < list.Values.Count; i++) { if (i > 0) ids.Append(','); ids.Append(list.Values[i].ToHex()); }
            return ids.ToString();
        }
        return value switch { null => string.Empty, bool flag => flag ? "true" : "false", IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty, _ => value.ToString() ?? string.Empty };
    }
    private static void AppendString(StringBuilder builder, string value) { builder.Append('"'); foreach (char c in value ?? string.Empty) { switch (c) { case '"': builder.Append("\\\""); break; case '\\': builder.Append("\\\\"); break; case '\n': builder.Append("\\n"); break; case '\r': builder.Append("\\r"); break; case '\t': builder.Append("\\t"); break; default: if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else builder.Append(c); break; } } builder.Append('"'); }
    private static string Sha256(byte[] payload) { using SHA256 sha = SHA256.Create(); return ToHex(sha.ComputeHash(payload)); }
    private static string ToHex(byte[] value) { var builder = new StringBuilder(value.Length * 2); for (int i = 0; i < value.Length; i++) builder.Append(value[i].ToString("x2", CultureInfo.InvariantCulture)); return builder.ToString(); }
    private static byte[] ParseHex(string value) { if (value.Length % 2 != 0) throw new FormatException("hex payload has odd length"); var result = new byte[value.Length / 2]; for (int i = 0; i < result.Length; i++) { int hi = Hex(value[i * 2]); int lo = Hex(value[i * 2 + 1]); if (hi < 0 || lo < 0) throw new FormatException("payload is not hex"); result[i] = (byte)((hi << 4) | lo); } return result; }
    private static int Hex(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    private sealed class JsonValue
    {
        private JsonValue(string? text, Dictionary<string, JsonValue>? properties, List<JsonValue>? items) { Text = text; Properties = properties; Items = items; }
        private readonly string? Text; private readonly Dictionary<string, JsonValue>? Properties; internal readonly List<JsonValue>? Items;
        internal string RequiredString(string key) { if (Properties is null || !Properties.TryGetValue(key, out JsonValue? value) || value.Text is null) throw new FormatException("missing string: " + key); return value.Text; }
        internal ulong RequiredU64(string key) { string text = RequiredStringNumber(key); if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value)) throw new FormatException("invalid u64: " + key); return value; }
        private string RequiredStringNumber(string key) { if (Properties is null || !Properties.TryGetValue(key, out JsonValue? value) || value.Text is null) throw new FormatException("missing number: " + key); return value.Text; }
        internal List<JsonValue> RequiredArray(string key) { if (Properties is null || !Properties.TryGetValue(key, out JsonValue? value) || value.Items is null) throw new FormatException("missing array: " + key); return value.Items; }
        internal static JsonValue Parse(string text) { var parser = new Parser(text); JsonValue result = parser.Value(); parser.Skip(); if (!parser.End) throw new FormatException("trailing JSON"); return result; }
        private sealed class Parser
        {
            private readonly string _text; private int _index; internal Parser(string text) => _text = text; internal bool End { get { Skip(); return _index == _text.Length; } }
            internal void Skip() { while (_index < _text.Length && char.IsWhiteSpace(_text[_index])) _index++; }
            internal JsonValue Value() { Skip(); if (_index >= _text.Length) throw new FormatException("JSON value missing"); char c = _text[_index]; if (c == '{') return Object(); if (c == '[') return Array(); if (c == '"') return new JsonValue(String(), null, null); int start = _index; while (_index < _text.Length && ",]}".IndexOf(_text[_index]) < 0 && !char.IsWhiteSpace(_text[_index])) _index++; return new JsonValue(_text.Substring(start, _index - start), null, null); }
            private JsonValue Object() { _index++; var values = new Dictionary<string, JsonValue>(StringComparer.Ordinal); Skip(); if (Take('}')) return new JsonValue(null, values, null); while (true) { Skip(); string key = String(); Skip(); Need(':'); JsonValue value = Value(); if (!values.TryAdd(key, value)) throw new FormatException("duplicate JSON key"); Skip(); if (Take('}')) return new JsonValue(null, values, null); Need(','); } }
            private JsonValue Array() { _index++; var values = new List<JsonValue>(); Skip(); if (Take(']')) return new JsonValue(null, null, values); while (true) { values.Add(Value()); Skip(); if (Take(']')) return new JsonValue(null, null, values); Need(','); } }
            private string String() { Need('"'); var b = new StringBuilder(); while (_index < _text.Length) { char c = _text[_index++]; if (c == '"') return b.ToString(); if (c != '\\') { b.Append(c); continue; } if (_index >= _text.Length) throw new FormatException("unterminated JSON string"); char e = _text[_index++]; b.Append(e switch { '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'u' => Unicode(), _ => throw new FormatException("invalid JSON escape") }); } throw new FormatException("unterminated JSON string"); }
            private char Unicode() { if (_index + 4 > _text.Length) throw new FormatException("invalid unicode escape"); int value = int.Parse(new string(_text.AsSpan(_index, 4)), NumberStyles.HexNumber, CultureInfo.InvariantCulture); _index += 4; return (char)value; }
            private bool Take(char c) { if (_index < _text.Length && _text[_index] == c) { _index++; return true; } return false; }
            private void Need(char c) { Skip(); if (_index >= _text.Length || _text[_index] != c) throw new FormatException("expected JSON token"); _index++; }
        }
    }
}

public sealed class ConnectionSupersededMessage : WorldMessage
{
    public ConnectionSupersededMessage(NetEntityId netEntityId, ulong newConnectionGeneration) { NetEntityId = netEntityId; NewConnectionGeneration = newConnectionGeneration; }
    public NetEntityId NetEntityId { get; }
    public ulong NewConnectionGeneration { get; }
}

public sealed class ErrorMessage : WorldMessage
{
    public ErrorMessage(string code, string detail) { Code = code; Detail = detail; }
    public string Code { get; }
    public string Detail { get; }
}
