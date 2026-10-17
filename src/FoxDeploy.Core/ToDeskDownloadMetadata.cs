using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

// Read the data-only subset emitted by Nuxt; never evaluate the site's JavaScript.
internal static class ToDeskDownloadMetadata
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex NumberToken = new(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant, RegexTimeout);

    public static string Parse(string metadata)
    {
        if (metadata.Length > 2097152) throw Changed();
        try
        {
            var scripts = Regex.Matches(metadata,
                @"<script(?:\s[^>]*)?>\s*window\.__NUXT__\s*=\s*(?<payload>.*?)</script\s*>",
                RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);
            if (scripts.Count != 1) throw Changed();
            var payload = scripts[0].Groups["payload"].Value.Trim();
            var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string body;
            if (payload.StartsWith("(function", StringComparison.Ordinal))
            {
                var function = Regex.Match(payload,
                    @"^\(function\s*\((?<parameters>[^)]*)\)\s*\{\s*return\s+(?<body>.*)\}\s*\((?<arguments>.*)\)\s*\)\s*;?\s*$",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);
                if (!function.Success) throw Changed();
                var names = function.Groups["parameters"].Value.Split(',', StringSplitOptions.TrimEntries);
                if (names.Length > 128 || names.Any(name => !Regex.IsMatch(name, @"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant, RegexTimeout)))
                    throw Changed();
                using var arguments = JsonDocument.Parse("[" + function.Groups["arguments"].Value + "]", new JsonDocumentOptions { MaxDepth = 48 });
                if (names.Length != arguments.RootElement.GetArrayLength()) throw Changed();
                for (var index = 0; index < names.Length; index++)
                {
                    var value = arguments.RootElement[index];
                    if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object || !variables.TryAdd(names[index], value.Clone())) throw Changed();
                }
                body = function.Groups["body"].Value;
            }
            else body = payload.TrimEnd(';').Trim();

            using var data = new DataReader(body, variables).Read();
            if (data.RootElement.ValueKind != JsonValueKind.Object || !data.RootElement.TryGetProperty("data", out var pages) || pages.ValueKind != JsonValueKind.Array) throw Changed();
            var clients = pages.EnumerateArray().Where(page => page.ValueKind == JsonValueKind.Object && page.TryGetProperty("clientInfo", out _)).ToArray();
            if (clients.Length != 1) throw Changed();
            var client = clients[0].GetProperty("clientInfo");
            if (client.ValueKind != JsonValueKind.Object || !client.TryGetProperty("win_link", out var link) || link.ValueKind != JsonValueKind.String ||
                !client.TryGetProperty("win_version", out var version) || version.ValueKind != JsonValueKind.String) throw Changed();
            var stableVersion = version.GetString()!;
            if (!Regex.IsMatch(stableVersion, @"^[0-9]+(?:\.[0-9]+){3}$", RegexOptions.CultureInvariant, RegexTimeout)) throw Changed();
            var address = link.GetString()!;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.IdnHost != "dl.todesk.com" || uri.Query != "" || uri.Fragment != "" ||
                uri.AbsolutePath != "/irrigation/ToDesk_" + stableVersion + ".exe") throw Changed();
            return uri.AbsoluteUri;
        }
        catch (JsonException ex) { throw Changed(ex); }
    }

    private static InvalidDataException Changed(Exception? inner = null) =>
        new("ToDesk 官网个人版 Windows 稳定版信息已变化，无法确认正确安装包；已停止下载，不会改用其他产品或灰度包。", inner);

    // Only JSON data, unquoted object keys and references to literal IIFE arguments are accepted.
    // Calls, assignments, property access and arbitrary expressions fail closed.
    private sealed class DataReader(string source, IReadOnlyDictionary<string, JsonElement> variables)
    {
        private int _position;
        private int _nodes;

        public JsonDocument Read()
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                WriteValue(writer, 0);
                Whitespace();
                if (_position != source.Length) throw Changed();
            }
            return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
        }

        private void WriteValue(Utf8JsonWriter writer, int depth)
        {
            if (depth > 48 || ++_nodes > 16384 || writer.BytesCommitted + writer.BytesPending > 8388608) throw Changed();
            Whitespace();
            if (_position >= source.Length) throw Changed();
            var character = source[_position];
            if (character == '{')
            {
                _position++; writer.WriteStartObject(); Whitespace();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                if (!Consume('}'))
                {
                    do
                    {
                        Whitespace();
                        var key = Peek() == '"' ? ReadString() : ReadIdentifier();
                        if (!keys.Add(key)) throw Changed();
                        writer.WritePropertyName(key); Expect(':'); WriteValue(writer, depth + 1); Whitespace();
                    } while (Consume(','));
                    Expect('}');
                }
                writer.WriteEndObject();
            }
            else if (character == '[')
            {
                _position++; writer.WriteStartArray(); Whitespace();
                if (!Consume(']'))
                {
                    do { WriteValue(writer, depth + 1); Whitespace(); } while (Consume(','));
                    Expect(']');
                }
                writer.WriteEndArray();
            }
            else if (character == '"') writer.WriteStringValue(ReadString());
            else if (character is '-' or >= '0' and <= '9')
            {
                var number = NumberToken.Match(source, _position);
                if (!number.Success) throw Changed();
                writer.WriteRawValue(number.Value); _position += number.Length;
            }
            else
            {
                var name = ReadIdentifier();
                if (name == "true") writer.WriteBooleanValue(true);
                else if (name == "false") writer.WriteBooleanValue(false);
                else if (name == "null") writer.WriteNullValue();
                else if (variables.TryGetValue(name, out var value)) value.WriteTo(writer);
                else throw Changed();
            }
        }

        private string ReadString()
        {
            var start = _position++;
            while (_position < source.Length)
            {
                var character = source[_position++];
                if (character == '\\') { _position++; continue; }
                if (character == '"') return JsonSerializer.Deserialize<string>(source[start.._position]) ?? throw Changed();
            }
            throw Changed();
        }

        private string ReadIdentifier()
        {
            var start = _position;
            if (!(Peek() is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' or '$')) throw Changed();
            while (Peek() is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '$') _position++;
            return source[start.._position];
        }

        private char Peek() => _position < source.Length ? source[_position] : '\0';
        private void Whitespace() { while (char.IsWhiteSpace(Peek())) _position++; }
        private bool Consume(char character) { if (Peek() != character) return false; _position++; return true; }
        private void Expect(char character) { Whitespace(); if (!Consume(character)) throw Changed(); }
    }
}
