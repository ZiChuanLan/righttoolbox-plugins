using TableConvert;

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

var csv = "name,note\r\n\"Alice\",\"hello, \"\"world\"\"\"\r\nBob,\"line1\nline2\"";
var parsed = TableEngine.ParseDelimited(csv, ',');
Assert(parsed.Count == 3, $"Expected 3 rows, got {parsed.Count}");
Assert(parsed[1][1] == "hello, \"world\"", $"Unexpected quote parse: {parsed[1][1]}");
Assert(parsed[2][1] == "line1\nline2", "Unexpected multiline cell");

var md = TableEngine.ToMarkdownTable(parsed, true);
Assert(md.Contains("| name | note |"), "Missing markdown header");

var roundtripCsv = TableEngine.JsonToCsv("[{\"a\":1,\"b\":\"x\"},{\"a\":2,\"c\":true}]", out var count);
Assert(count == 2 && roundtripCsv.Contains("a,b,c"), "Unexpected JSON->CSV output");

Console.WriteLine("PASS table-convert unit tests");
