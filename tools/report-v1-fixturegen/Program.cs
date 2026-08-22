using System.Text;
using System.Text.Json;
using Blake3;

// Card t_957718d4 - gera fixtures/report-v1-exemplo.json conforme docs/schema-report-v1.md.
// Emite JSON no formato exato da §2: UTF-8 sem BOM, LF final, indent 2, chaves em ordem
// declarada (ordem de declaracao das propriedades), escapamento minimo RFC 8259.

var repoRoot = args[0];
var treeRoot = Path.Combine(repoRoot, "fixtures", "report-v1-tree");

string Blake3Hex(string relativePath)
{
    var bytes = File.ReadAllBytes(Path.Combine(treeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    return Hasher.Hash(bytes).ToString();
}

// ---------- conteudos reais da arvore ----------
var c1 = new byte[96];
for (int i = 0; i < 96; i++) c1[i] = (byte)((i * 7 + 3) % 256);
var xBase = new byte[262144];
for (int i = 0; i < 262144; i++) xBase[i] = (byte)(i % 251);
var x1 = (byte[])xBase.Clone();
var x2 = (byte[])xBase.Clone();
for (int i = 131072; i < 131088; i++) { x1[i] = 0xAA; x2[i] = 0xBB; }

// hashes BLAKE3 reais: dos arquivos em disco e dos conteudos sinteticos (cruzamento)
var hashDocs = Blake3Hex("docs/foto-reuniao.jpg");
var hashDocsBackup = Blake3Hex("docs/backup/foto-reuniao.jpg");
var hashFotos = Blake3Hex("fotos/foto-reuniao.jpg");
var hashX1Disk = Blake3Hex("projetos/orcamento.xlsx");
var hashX2Disk = Blake3Hex("projetos/orcamento-DESKTOP-ABC123 (conflicted copy).xlsx");
var hashN1 = Blake3Hex("notas/reuniao.txt");
var hashN2 = Blake3Hex("notas/arquivo morto/reuniao.txt");
var hashL = Blake3Hex("leiame.txt");

var hashC1Synthetic = Hasher.Hash(c1).ToString();
var hashX1Synthetic = Hasher.Hash(x1).ToString();
var hashX2Synthetic = Hasher.Hash(x2).ToString();

if (hashC1Synthetic != hashDocs || hashDocs != hashDocsBackup || hashDocsBackup != hashFotos)
    throw new Exception("C1: hash sintetico difere do hash em disco");
if (hashX1Synthetic != hashX1Disk) throw new Exception("X1: hash sintetico difere do disco");
if (hashX2Synthetic != hashX2Disk) throw new Exception("X2: hash sintetico difere do disco");
if (hashX1Disk == hashX2Disk) throw new Exception("X1 e X2 nao podem ter hash igual");

// ---------- ordenacao canonica por bytes UTF-8 ----------
static int CmpUtf8(string a, string b)
{
    var ba = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    int n = Math.Min(ba.Length, bb.Length);
    for (int i = 0; i < n; i++)
        if (ba[i] != bb[i]) return ba[i] < bb[i] ? -1 : 1;
    return ba.Length.CompareTo(bb.Length);
}

// ---------- modelo na ordem declarada ----------
var report = new Report
{
    ReportSchemaVersion = 1,
    Algorithm = "BLAKE3",
    HashVersion = 1,
    NormalizationRulesVersion = 1,
    GeneratedFrom = new GeneratedFrom
    {
        RootPath = "fixtures/report-v1-tree",
        ScanStartedUtc = "2026-08-22T19:40:00.000Z",
        ScanFinishedUtc = "2026-08-22T19:40:00.041Z",
    },
    Telemetry = new Telemetry
    {
        FilesEnumerated = 10,
        FilesPlaceholder = 2,
        FilesSkipped = 1,
        FilesPartialHashed = 7,
        FilesFullHashed = 5,
        BytesRead = 787496,
        BytesReadPartial = 262632,
        BytesReadFull = 524864,
        PlaceholderBytesRead = 0,
    },
    Groups = new List<Group>
    {
        new()
        {
            NormalizedBaseName = "foto-reuniao.jpg",
            SizeBytes = 96,
            Members = new List<MemberPath>
            {
                new() { Path = "docs/backup/foto-reuniao.jpg" },
                new() { Path = "docs/foto-reuniao.jpg" },
                new() { Path = "fotos/foto-reuniao.jpg" },
            },
        },
        new()
        {
            NormalizedBaseName = "orcamento.xlsx",
            SizeBytes = 262144,
            Members = new List<MemberPath>
            {
                new() { Path = "projetos/orcamento-DESKTOP-ABC123 (conflicted copy).xlsx" },
                new() { Path = "projetos/orcamento.xlsx" },
            },
        },
        new()
        {
            NormalizedBaseName = "reuniao.txt",
            SizeBytes = 44,
            Members = new List<MemberPath>
            {
                new() { Path = "notas/arquivo morto/reuniao.txt" },
                new() { Path = "notas/reuniao.txt" },
            },
        },
    },
    IdenticalDuplicates = new List<IdenticalDuplicate>
    {
        new()
        {
            Hash = hashC1Synthetic,
            SizeBytes = 96,
            Files = new List<string>
            {
                "docs/backup/foto-reuniao.jpg",
                "docs/foto-reuniao.jpg",
                "fotos/foto-reuniao.jpg",
            },
        },
    },
    RealConflicts = new List<RealConflict>
    {
        new()
        {
            NormalizedBaseName = "orcamento.xlsx",
            SizeBytes = 262144,
            Files = new List<ConflictFile>
            {
                new() { Path = "projetos/orcamento-DESKTOP-ABC123 (conflicted copy).xlsx", Hash = hashX2Synthetic },
                new() { Path = "projetos/orcamento.xlsx", Hash = hashX1Synthetic },
            },
        },
    },
    Placeholders = new List<PlaceholderEntry>
    {
        new() { Path = "arquivo morto/relatorio antigo.docx", Kinds = new List<string> { "offline" }, SizeBytes = 10485760 },
        new() { Path = "arquivos grandes/video-aula.mp4", Kinds = new List<string> { "recall_on_data_access" }, SizeBytes = 524288000 },
    },
};

// verificacao das ordens canonicas
if (CmpUtf8(report.Groups[0].NormalizedBaseName, report.Groups[1].NormalizedBaseName) >= 0 ||
    CmpUtf8(report.Groups[1].NormalizedBaseName, report.Groups[2].NormalizedBaseName) >= 0)
    throw new Exception("groups fora de ordem");
if (CmpUtf8("arquivo morto/relatorio antigo.docx", "arquivos grandes/video-aula.mp4") >= 0)
    throw new Exception("placeholders fora de ordem");

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

string json = JsonSerializer.Serialize(report, options);
File.WriteAllText(Path.Combine(repoRoot, "fixtures", "report-v1-exemplo.json"), json + "\n",
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine("hash C1  = " + hashC1Synthetic);
Console.WriteLine("hash X1  = " + hashX1Synthetic);
Console.WriteLine("hash X2  = " + hashX2Synthetic);
Console.WriteLine("hash N1  = " + hashN1);
Console.WriteLine("hash N2  = " + hashN2);
Console.WriteLine("hash L   = " + hashL);
Console.WriteLine("fixture escrito");

// ---------- POCOs: ordem de declaracao = ordem de emissao ----------
public class Report
{
    public int ReportSchemaVersion { get; set; }
    public string Algorithm { get; set; } = "";
    public int HashVersion { get; set; }
    public int NormalizationRulesVersion { get; set; }
    public GeneratedFrom GeneratedFrom { get; set; } = new();
    public Telemetry Telemetry { get; set; } = new();
    public List<Group> Groups { get; set; } = new();
    public List<IdenticalDuplicate> IdenticalDuplicates { get; set; } = new();
    public List<RealConflict> RealConflicts { get; set; } = new();
    public List<PlaceholderEntry> Placeholders { get; set; } = new();
}

public class GeneratedFrom
{
    public string RootPath { get; set; } = "";
    public string ScanStartedUtc { get; set; } = "";
    public string ScanFinishedUtc { get; set; } = "";
}

public class Telemetry
{
    public int FilesEnumerated { get; set; }
    public int FilesPlaceholder { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesPartialHashed { get; set; }
    public int FilesFullHashed { get; set; }
    public long BytesRead { get; set; }
    public long BytesReadPartial { get; set; }
    public long BytesReadFull { get; set; }
    public int PlaceholderBytesRead { get; set; }
}

public class Group
{
    public string NormalizedBaseName { get; set; } = "";
    public long SizeBytes { get; set; }
    public List<MemberPath> Members { get; set; } = new();
}

public class MemberPath
{
    public string Path { get; set; } = "";
}

public class IdenticalDuplicate
{
    public string Hash { get; set; } = "";
    public long SizeBytes { get; set; }
    public List<string> Files { get; set; } = new();
}

public class RealConflict
{
    public string NormalizedBaseName { get; set; } = "";
    public long SizeBytes { get; set; }
    public List<ConflictFile> Files { get; set; } = new();
}

public class ConflictFile
{
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
}

public class PlaceholderEntry
{
    public string Path { get; set; } = "";
    public List<string> Kinds { get; set; } = new();
    public long SizeBytes { get; set; }
}
