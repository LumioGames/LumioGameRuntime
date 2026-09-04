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
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
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
                if (rpc.Args.Count > 64) throw new ArgumentException("RPC argument count exceeds the C-1 limit.", nameof(message));
                json.Append("{\"appliedTick\":").Append(rpc.AppliedTick.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"args\":[");
                for (int a = 0; a < rpc.Args.Count; a++)
                {
                    if (a > 0) json.Append(',');
                    AppendString(json, ToHex(StrictUtf8.GetBytes(rpc.Args[a]?.ToString() ?? string.Empty)));
                }
                json.Append(']');
                json.Append(",\"componentId\":"); AppendString(json, rpc.ComponentId);
                json.Append(",\"messageId\":").Append(rpc.MessageId.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"method\":"); AppendString(json, rpc.Method);
                json.Append(",\"roomSequence\":").Append(rpc.RoomSequence.ToString(CultureInfo.InvariantCulture));
                json.Append(",\"scope\":"); AppendString(json, ScopeText(rpc.Scope));
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
            if (!IsKnownErrorCode(error.Code)) throw new ArgumentException("Unknown C-1 error code.", nameof(message));
            json.Append("{\"code\":"); AppendString(json, error.Code); json.Append(",\"detail\":"); AppendString(json, error.Detail); json.Append(",\"messageType\":\"Error\"}");
            return Encoding.UTF8.GetBytes(json.ToString());
        }
        throw new ArgumentException("Unsupported C-1 message type.", nameof(message));
    }

    public static WorldMessage DecodePack(ReadOnlySpan<byte> bytes)
    {
        JsonValue root = JsonValue.Parse(DecodeUtf8(bytes));
        string type = root.RequiredString("messageType");
        if (type == "Welcome")
        {
            root.EnsureKeys("connectionGeneration", "instanceId", "messageType", "selfNetEntityId");
            if (!NetEntityId.TryParse(root.RequiredString("selfNetEntityId"), out NetEntityId self)) throw new FormatException("selfNetEntityId must be 128-bit hex.");
            return new WelcomeMessage(root.RequiredU64("instanceId"), self, root.RequiredU64("connectionGeneration"));
        }
        if (type == "WorldChange") return DecodeWorldChange(root);
        if (type == "InputCommand") return DecodeInput(bytes);
        if (type == "ConnectionSuperseded") { root.EnsureKeys("messageType", "netEntityId", "newConnectionGeneration", "reasonCode"); if (root.RequiredString("reasonCode") != "connection_superseded") throw new FormatException("invalid reasonCode"); return new ConnectionSupersededMessage(NetEntityId.Parse(root.RequiredString("netEntityId")), root.RequiredU64("newConnectionGeneration")); }
        if (type == "Error") { root.EnsureKeys("code", "detail", "messageType"); string code = root.RequiredString("code"); if (!IsKnownErrorCode(code)) throw new FormatException("unknown error code"); return new ErrorMessage(code, root.RequiredString("detail")); }
        throw new FormatException("unknown messageType: " + type);
    }

    public static byte[] EncodeInput(InputCommandMessage input)
    {
        if (input.Commands.Count > 16) throw new ArgumentException("command count exceeds the C-1 limit.", nameof(input));
        var json = new StringBuilder("{\"commands\":[");
        for (int i = 0; i < input.Commands.Count; i++)
        {
            if (i > 0) json.Append(',');
            InputCommandPart command = input.Commands[i];
            byte[] payload = command.Payload.ToArray();
            json.Append("{\"mappingId\":"); AppendString(json, command.MappingId);
            json.Append(",\"payload\":"); AppendString(json, ToHex(payload));
            json.Append(",\"payloadSha256\":"); AppendString(json, Sha256(payload));
            json.Append('}');
        }
        json.Append("],\"messageType\":\"InputCommand\"}");
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    public static InputCommandMessage DecodeInput(ReadOnlySpan<byte> bytes)
        => DecodeInput(bytes, default);

    /// <summary>Decodes an input envelope while the host supplies the authenticated sender identity.</summary>
    public static InputCommandMessage DecodeInput(ReadOnlySpan<byte> bytes, NetEntityId sender)
    {
        JsonValue root = JsonValue.Parse(DecodeUtf8(bytes));
        root.EnsureKeys("commands", "messageType");
        if (root.RequiredString("messageType") != "InputCommand") throw new FormatException("messageType must be InputCommand.");
        List<JsonValue> commands = root.RequiredArray("commands");
        if (commands.Count > 16) throw new FormatException("too many commands");
        var decoded = new List<InputCommandPart>(commands.Count);
        for (int i = 0; i < commands.Count; i++)
        {
            JsonValue command = commands[i];
            command.EnsureKeys("mappingId", "payload", "payloadSha256");
            string mapping = command.RequiredString("mappingId");
            if (mapping != ChatInput && mapping != FieldWrite && mapping != ServerRpc) throw new FormatException("unknown command mapping.");
            byte[] payload = ParseHex(command.RequiredString("payload"));
            if (!string.Equals(Sha256(payload), command.RequiredString("payloadSha256"), StringComparison.Ordinal)) throw new FormatException("bad_payload_hash");
            if (mapping == ChatInput && (!TryReadUtf8Payload(payload, out string text) || StrictUtf8.GetByteCount(text) > 512)) throw new FormatException("undecodable_payload");
            if (mapping == FieldWrite && !TryDecodeFieldWrite(payload, out _, out _, out _, out _)) throw new FormatException("undecodable_payload");
            if (mapping == ServerRpc && !TryDecodeServerRpc(payload, out _, out _, out _)) throw new FormatException("undecodable_payload");
            decoded.Add(new InputCommandPart(mapping, payload));
        }
        return new InputCommandMessage(sender, decoded);
    }

    private static WorldChangeMessage DecodeWorldChange(JsonValue root)
    {
        root.EnsureKeys("creates", "destroys", "fields", "messageType", "rpcs", "tick");
        var creates = new List<CreateRecord>();
        foreach (JsonValue item in root.RequiredArray("creates"))
        {
            item.EnsureKeys("entityType", "fields", "netEntityId");
            var fields = new List<FieldValue>();
            foreach (JsonValue field in item.RequiredArray("fields"))
            {
                field.EnsureKeys("componentId", "fieldId", "value");
                fields.Add(new FieldValue(field.RequiredString("componentId"), field.RequiredString("fieldId"), field.RequiredString("value")));
            }
            creates.Add(new CreateRecord(item.RequiredString("entityType"), NetEntityId.Parse(item.RequiredString("netEntityId")), fields));
        }
        var changed = new List<FieldChange>();
        foreach (JsonValue field in root.RequiredArray("fields"))
        {
            field.EnsureKeys("componentId", "fieldId", "netEntityId", "reason", "value");
            string reason = field.RequiredString("reason");
            if (reason != "sync" && reason != "correction") throw new FormatException("unknown change reason");
            changed.Add(new FieldChange(NetEntityId.Parse(field.RequiredString("netEntityId")), field.RequiredString("componentId"), field.RequiredString("fieldId"), field.RequiredString("value"), reason == "correction" ? ChangeReason.Correction : ChangeReason.Sync));
        }
        var destroys = new List<NetEntityId>();
        foreach (JsonValue item in root.RequiredArray("destroys")) { item.EnsureKeys("netEntityId"); destroys.Add(NetEntityId.Parse(item.RequiredString("netEntityId"))); }
        var rpcs = new List<ClientRpcRecord>();
        foreach (JsonValue item in root.RequiredArray("rpcs"))
        {
            item.EnsureKeys("appliedTick", "args", "componentId", "messageId", "method", "roomSequence", "scope", "sender", "target");
            List<JsonValue> args = item.RequiredArray("args");
            if (args.Count > 64) throw new FormatException("too many RPC arguments");
            var decodedArgs = new object?[args.Count];
            for (int a = 0; a < args.Count; a++) decodedArgs[a] = DecodeUtf8(ParseHex(args[a].AsString()));
            rpcs.Add(new ClientRpcRecord(NetEntityId.Parse(item.RequiredString("target")), item.RequiredString("componentId"), item.RequiredString("method"), decodedArgs, item.RequiredU64("messageId"), item.RequiredU64("roomSequence"), NetEntityId.Parse(item.RequiredString("sender")), item.RequiredU64("appliedTick"), ParseScope(item.RequiredString("scope"))));
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
        try { text = StrictUtf8.GetString(payload.Slice(4)); }
        catch (DecoderFallbackException) { return false; }
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

    internal static byte[] EncodeServerRpc(string componentId, string method, string argument) => EncodeServerRpc(componentId, method, new object?[] { argument });

    internal static byte[] EncodeServerRpc(string componentId, string method, object?[] args)
    {
        byte[][] values = new byte[args.Length][];
        int size = 4 + StrictUtf8.GetByteCount(componentId) + 4 + StrictUtf8.GetByteCount(method) + 4;
        for (int i = 0; i < args.Length; i++) { values[i] = StrictUtf8.GetBytes(args[i]?.ToString() ?? string.Empty); size += 4 + values[i].Length; }
        byte[] result = new byte[size];
        int offset = 0;
        WriteBytes(result, ref offset, StrictUtf8.GetBytes(componentId));
        WriteBytes(result, ref offset, StrictUtf8.GetBytes(method));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset, 4), (uint)values.Length); offset += 4;
        for (int i = 0; i < values.Length; i++) WriteBytes(result, ref offset, values[i]);
        return result;
    }

    internal static bool TryDecodeServerRpc(ReadOnlySpan<byte> payload, out string componentId, out string method, out string[] arguments)
    {
        componentId = method = string.Empty; arguments = Array.Empty<string>(); int offset = 0;
        if (!TryReadString(payload, ref offset, out componentId) || !TryReadString(payload, ref offset, out method) || !TryReadU32(payload, ref offset, out uint count) || count > 64) return false;
        var values = new string[count];
        for (int i = 0; i < values.Length; i++) if (!TryReadString(payload, ref offset, out values[i])) return false;
        if (offset != payload.Length) return false;
        arguments = values; return true;
    }

    private static void WriteBytes(byte[] target, ref int offset, byte[] value) { BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), (uint)value.Length); offset += 4; Buffer.BlockCopy(value, 0, target, offset, value.Length); offset += value.Length; }
    private static bool TryReadU64(ReadOnlySpan<byte> payload, ref int offset, out ulong value) { value = 0; if (offset + 8 > payload.Length) return false; value = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8)); offset += 8; return true; }
    private static bool TryReadU32(ReadOnlySpan<byte> payload, ref int offset, out uint value) { value = 0; if (offset + 4 > payload.Length) return false; value = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)); offset += 4; return true; }
    private static bool TryReadString(ReadOnlySpan<byte> payload, ref int offset, out string value) { value = string.Empty; if (offset + 4 > payload.Length) return false; uint len = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)); offset += 4; if (len > (uint)(payload.Length - offset)) return false; try { value = StrictUtf8.GetString(payload.Slice(offset, (int)len)); } catch (DecoderFallbackException) { return false; } offset += (int)len; return true; }
    private static string ValueText(object? value)
    {
        if (value is ISyncContainer container) return ContainerText(container);
        if (value is IReadOnlyList<NetEntityId> ids)
        {
            var text = new StringBuilder();
            for (int i = 0; i < ids.Count; i++) { if (i > 0) text.Append(','); text.Append(ids[i].ToHex()); }
            return text.ToString();
        }
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var text = new StringBuilder();
            foreach (object? item in enumerable)
            {
                if (item is NetEntityId id) { if (text.Length > 0) text.Append(','); text.Append(id.ToHex()); continue; }
                object? key = item?.GetType().GetProperty("Key")?.GetValue(item);
                object? itemValue = item?.GetType().GetProperty("Value")?.GetValue(item);
                if (key is not null) { if (text.Length > 0) text.Append(';'); text.Append(key).Append('=').Append(itemValue); }
            }
            return text.ToString();
        }
        if (value is SyncList<NetEntityId> list)
        {
            var listText = new StringBuilder();
            for (int i = 0; i < list.Values.Count; i++) { if (i > 0) listText.Append(','); listText.Append(list.Values[i].ToHex()); }
            return listText.ToString();
        }
        return value switch { null => string.Empty, bool flag => flag ? "true" : "false", IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty, _ => value.ToString() ?? string.Empty };
    }
    internal static string ContainerText(ISyncContainer container)
    {
        object? snapshot = container.BoxedValue;
        if (snapshot is IReadOnlyList<NetEntityId> ids)
        {
            var text = new StringBuilder();
            for (int i = 0; i < ids.Count; i++) { if (i > 0) text.Append(','); text.Append(ids[i].ToHex()); }
            return text.ToString();
        }
        if (snapshot is IReadOnlyDictionary<string, string> strings)
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in strings) { if (text.Length > 0) text.Append(';'); text.Append(pair.Key).Append('=').Append(pair.Value); }
            return text.ToString();
        }
        if (snapshot is System.Collections.IEnumerable enumerable && snapshot is not string)
        {
            var text = new StringBuilder();
            foreach (object? item in enumerable)
            {
                if (item is NetEntityId id) { if (text.Length > 0) text.Append(','); text.Append(id.ToHex()); continue; }
                object? key = item?.GetType().GetProperty("Key")?.GetValue(item);
                object? itemValue = item?.GetType().GetProperty("Value")?.GetValue(item);
                if (key is not null) { if (text.Length > 0) text.Append(';'); text.Append(key).Append('=').Append(itemValue); }
            }
            return text.ToString();
        }
        return snapshot?.ToString() ?? string.Empty;
    }
    internal static object ParseContainerText(ISyncContainer container, string text)
    {
        if (container.ValueType == typeof(SyncList<NetEntityId>))
        {
            var list = new SyncList<NetEntityId>(container.Scope, container.Authority, container.Notify, container.ClaimBy);
            foreach (string token in (text ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!NetEntityId.TryParse(token, out NetEntityId id)) throw new FormatException("invalid container id");
                list.Add(id);
            }
            return list;
        }
        return text ?? string.Empty;
    }
    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new FormatException("invalid UTF-8", ex); }
    }
    private static void AppendString(StringBuilder builder, string value) { builder.Append('"'); foreach (char c in value ?? string.Empty) { switch (c) { case '"': builder.Append("\\\""); break; case '\\': builder.Append("\\\\"); break; case '\n': builder.Append("\\n"); break; case '\r': builder.Append("\\r"); break; case '\t': builder.Append("\\t"); break; default: if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); else builder.Append(c); break; } } builder.Append('"'); }
    private static string Sha256(byte[] payload) { using SHA256 sha = SHA256.Create(); return ToHex(sha.ComputeHash(payload)); }
    private static string ToHex(byte[] value) { var builder = new StringBuilder(value.Length * 2); for (int i = 0; i < value.Length; i++) builder.Append(value[i].ToString("x2", CultureInfo.InvariantCulture)); return builder.ToString(); }
    private static byte[] ParseHex(string value) { if (value.Length % 2 != 0) throw new FormatException("hex payload has odd length"); var result = new byte[value.Length / 2]; for (int i = 0; i < result.Length; i++) { int hi = Hex(value[i * 2]); int lo = Hex(value[i * 2 + 1]); if (hi < 0 || lo < 0) throw new FormatException("payload is not hex"); result[i] = (byte)((hi << 4) | lo); } return result; }
    private static int Hex(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
    private static bool IsKnownErrorCode(string code) => code is "bad_envelope" or "unsupported_contract" or "unknown_command_type" or "bad_payload_hash" or "undecodable_payload" or "block_order_violation" or "state_block_kind_mismatch" or "chat_text_too_long" or "chat_rate_exceeded" or "queue_full" or "session_closed" or "runtime_failure";
    private static string ScopeText(Scope scope) => scope switch { Scope.Room => "room", Scope.Aoi => "aoi", Scope.Owner => "owner", Scope.Claim => "claim", _ => throw new ArgumentOutOfRangeException(nameof(scope)) };
    private static Scope ParseScope(string value) => value switch { "room" => Scope.Room, "aoi" => Scope.Aoi, "owner" => Scope.Owner, "claim" => Scope.Claim, _ => throw new FormatException("unknown rpc scope") };

    private sealed class JsonValue
    {
        private JsonValue(string? text, Dictionary<string, JsonValue>? properties, List<JsonValue>? items) { Text = text; Properties = properties; Items = items; }
        private readonly string? Text; private readonly Dictionary<string, JsonValue>? Properties; internal readonly List<JsonValue>? Items;
        internal string AsString() => Text ?? throw new FormatException("expected string");
        internal void EnsureKeys(params string[] expected)
        {
            if (Properties is null) throw new FormatException("expected object");
            var allowed = new HashSet<string>(expected, StringComparer.Ordinal);
            foreach (string key in Properties.Keys) if (!allowed.Contains(key)) throw new FormatException("unknown field: " + key);
            for (int i = 0; i < expected.Length; i++) if (!Properties.ContainsKey(expected[i])) throw new FormatException("missing field: " + expected[i]);
        }
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
            private string String() { Need('"'); var b = new StringBuilder(); while (_index < _text.Length) { char c = _text[_index++]; if (c == '"') return b.ToString(); if (c < 0x20) throw new FormatException("unescaped JSON control character"); if (c != '\\') { b.Append(c); continue; } if (_index >= _text.Length) throw new FormatException("unterminated JSON string"); char e = _text[_index++]; b.Append(e switch { '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'u' => Unicode(), _ => throw new FormatException("invalid JSON escape") }); } throw new FormatException("unterminated JSON string"); }
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
