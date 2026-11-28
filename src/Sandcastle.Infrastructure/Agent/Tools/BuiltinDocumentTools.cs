using System.Text.Json;
using System.Text.RegularExpressions;

using Sandcastle.Application.Agent;

namespace Sandcastle.Infrastructure.Agent.Tools;

/// <summary>
/// SPEC-192: bounded rich document readers. Extraction runs inside the sandbox
/// (python stdlib + optional PDF extractor); archive members are never written
/// back to the workspace and SQLite access is read-only.
/// </summary>
public static partial class BuiltinDocumentTools
{
    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static string? Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int Int(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : fallback;

    private static string Sh(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string? WorkspacePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\0')) return null;
        var path = raw.Replace('\\', '/');
        if (path.StartsWith("/workspace/", StringComparison.Ordinal))
        {
            path = path["/workspace/".Length..];
        }
        else if (path.StartsWith('/')) return null;
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => p is ".." or ".")) return null;
        return path.Length is > 0 and <= 512 ? path : null;
    }

    private static async Task<ToolResult> RunPythonAsync(
        ToolCallContext ctx, string script, IReadOnlyList<string> args,
        int timeoutSeconds, CancellationToken ct)
    {
        var command = "cd /workspace/repo 2>/dev/null || cd /workspace; python3 - "
            + string.Join(' ', args.Select(Sh)) + " <<'PY'\n" + script + "\nPY";
        var result = await ctx.Executor.ExecAsync(ctx.Sandbox, command,
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120)), ct);
        if (result.TimedOut) return ToolResult.Error("document_read_timeout");
        var stdout = result.StdOut.Trim();
        if (result.ExitCode != 0)
        {
            return ToolResult.Error(string.IsNullOrWhiteSpace(result.StdErr)
                ? "document_read_failed" : SecretRedactor.Redact(result.StdErr.Trim()));
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return ToolResult.Ok(JsonSerializer.Deserialize<object>(
                RedactJson(doc.RootElement.GetRawText()))!);
        }
        catch (Exception)
        {
            return ToolResult.Error("document_reader_invalid_output");
        }
    }

    private static string RedactJson(string json) =>
        TokenPattern().Replace(SecretRedactor.Redact(json), "***");

    [GeneratedRegex("(?:sk-[A-Za-z0-9_-]{8,}|gh[pousr]_[A-Za-z0-9_]{8,}|github_pat_[A-Za-z0-9_]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    private const string PdfScript = """
import sys,os,json,hashlib,subprocess,shutil,tempfile
p,max_pages,max_chars=sys.argv[1],int(sys.argv[2]),int(sys.argv[3])
real=os.path.realpath(p); root=os.path.realpath(os.getcwd())
if not (real==root or real.startswith(root+os.sep)): raise SystemExit('workspace_path_invalid')
if not os.path.isfile(real): raise SystemExit('file_not_found')
size=os.path.getsize(real)
if size>8*1024*1024: raise SystemExit('file_too_large')
sha=hashlib.sha256(open(real,'rb').read()).hexdigest()
err=None; pages=[]
try:
    try:
        from pypdf import PdfReader
    except ImportError:
        from PyPDF2 import PdfReader
    reader=PdfReader(real)
    if getattr(reader,'is_encrypted',False):
        try:
            if reader.decrypt('')==0: raise ValueError('encrypted_pdf')
        except Exception: raise ValueError('encrypted_pdf')
    for i in range(min(len(reader.pages),max_pages)):
        try: txt=reader.pages[i].extract_text() or ''
        except Exception as ex: txt='[page extraction failed: '+type(ex).__name__+']'
        pages.append((i+1,txt))
    extractor='pypdf'
except ImportError:
    if shutil.which('pdftotext'):
        extractor='pdftotext'
        with tempfile.TemporaryDirectory() as td:
            out=os.path.join(td,'out.txt')
            cp=subprocess.run(['pdftotext','-f','1','-l',str(max_pages),real,out],capture_output=True,text=True,timeout=20)
            if cp.returncode!=0: raise SystemExit('pdf_extract_failed: '+(cp.stderr or 'pdftotext failed')[:200])
            text=open(out,encoding='utf-8',errors='replace').read()
        chunks=text.split('\f')
        pages=[(i+1,x) for i,x in enumerate(chunks[:max_pages])]
    else:
        # stdlib fallback for simple uncompressed/deflated PDFs; pypdf remains
        # the full-fidelity optional extractor.
        raw=open(real,'rb').read()
        if b'/Encrypt' in raw: raise SystemExit('encrypted_pdf')
        import re,zlib
        texts=[]
        for m in re.finditer(rb'stream\r?\n(.*?)\r?\nendstream',raw,re.S):
            data=m.group(1)
            try: data=zlib.decompress(data)
            except Exception: pass
            for tm in re.finditer(rb'\((?:\\.|[^\\)])*\)\s*Tj',data):
                token=tm.group(0)
                val=token[1:token.rfind(b')')].decode('latin1',errors='replace')
                val=val.replace('\\(','(').replace('\\)',')').replace('\\n','\n').replace('\\\\','\\')
                texts.append(val)
        if not texts: raise SystemExit('unsupported_pdf: install pypdf in the sandbox image or provide pdftotext')
        extractor='stdlib-simple'
        pages=[(1,'\n'.join(texts))]
except ValueError as ex:
    raise SystemExit(str(ex))
except Exception as ex:
    raise SystemExit('pdf_extract_failed: '+type(ex).__name__)
trunc=False; out=[]; used=0
for n,txt in pages:
    marker='\n[page %d]\n'%n; remain=max_chars-used-len(marker)
    if remain<=0: trunc=True; break
    part=txt[:remain]
    if len(part)<len(txt): trunc=True
    out.append(marker+part); used+=len(marker)+len(part)
print(json.dumps({'path':p,'size_bytes':size,'sha256':sha,'extractor':extractor,'pages_read':len(out),'text':''.join(out),'truncated':trunc},ensure_ascii=False))
""";

    private const string ArchiveScript = """
import sys,os,json,hashlib,zipfile,tarfile
p,action,entry,max_entries,max_chars=sys.argv[1],sys.argv[2],sys.argv[3] or None,int(sys.argv[4]),int(sys.argv[5])
real=os.path.realpath(p); root=os.path.realpath(os.getcwd())
if not (real==root or real.startswith(root+os.sep)): raise SystemExit('workspace_path_invalid')
if not os.path.isfile(real): raise SystemExit('file_not_found')
size=os.path.getsize(real)
if size>32*1024*1024: raise SystemExit('file_too_large')
sha=hashlib.sha256(open(real,'rb').read()).hexdigest()
try:
    if zipfile.is_zipfile(real):
        z=zipfile.ZipFile(real); kind='zip'; infos=z.infolist(); names=[i.filename for i in infos]
        def read(name):
            i=z.getinfo(name); data=z.read(i)[:max_chars*2]; return len(data),(i.flag_bits&1)!=0,data
    elif tarfile.is_tarfile(real):
        z=tarfile.open(real); kind='tar'; infos=[i for i in z.getmembers() if i.isfile()]; names=[i.name for i in infos]
        def read(name):
            m=z.getmember(name); data=z.extractfile(m).read(max_chars*2); return len(data),False,data
    else: raise SystemExit('unsupported_archive')
except SystemExit: raise
except Exception as ex: raise SystemExit('archive_read_failed: '+type(ex).__name__)
base={'path':p,'size_bytes':size,'sha256':sha,'archive_kind':kind,'entries_total':len(names)}
if action=='list':
    base['entries']=[{'path':n,'truncated_list':len(names)>max_entries} for n in names[:max_entries]]
    base['entries_returned']=len(base['entries']); base['truncated']=len(names)>max_entries
    print(json.dumps(base,ensure_ascii=False)); raise SystemExit
if not entry: raise SystemExit('entry_required')
if entry not in names or entry.startswith('/') or '..' in entry.split('/'): raise SystemExit('entry_not_found_or_unsafe')
ln,enc,data=read(entry)
binary=b'\0' in data[:2048]
text='[binary entry — text omitted]' if binary else data.decode('utf-8',errors='replace')[:max_chars]
base.update({'entry':entry,'entry_bytes':ln,'encrypted':enc,'binary':binary,'text':text,'truncated':ln>max_chars*2 or len(text)>=max_chars})
print(json.dumps(base,ensure_ascii=False))
""";

    private const string SqliteScript = """
import sys,os,json,hashlib,sqlite3
p,action,sql,max_rows,max_chars=sys.argv[1],sys.argv[2],sys.argv[3],int(sys.argv[4]),int(sys.argv[5])
real=os.path.realpath(p); root=os.path.realpath(os.getcwd())
if not (real==root or real.startswith(root+os.sep)): raise SystemExit('workspace_path_invalid')
if not os.path.isfile(real): raise SystemExit('file_not_found')
size=os.path.getsize(real)
if size>64*1024*1024: raise SystemExit('file_too_large')
sha=hashlib.sha256(open(real,'rb').read()).hexdigest()
try:
    uri='file:'+real+'?mode=ro&immutable=1'
    db=sqlite3.connect(uri,uri=True); db.row_factory=sqlite3.Row
    base={'path':p,'size_bytes':size,'sha256':sha,'engine':'sqlite'}
    if action=='tables':
        rows=db.execute("select name,sql from sqlite_master where type='table' and name not like 'sqlite_%' order by name").fetchall()
        base['tables']=[{'name':r['name'],'schema':r['sql']} for r in rows[:100]]
        base['tables_returned']=len(base['tables']); base['truncated']=len(rows)>100
    elif action=='query':
        q=' '.join(sql.strip().split())
        if not (q.lower().startswith('select') or q.lower().startswith('with')) or ';' in q[:-1]: raise SystemExit('read_only_select_required')
        cur=db.execute(q); rows=cur.fetchmany(max_rows+1)
        cols=[d[0] for d in cur.description or []]
        vals=[[None if x is None else (x.hex() if isinstance(x,bytes) else str(x)) for x in row] for row in rows[:max_rows]]
        text=json.dumps({'columns':cols,'rows':vals},ensure_ascii=False)
        if len(text)>max_chars: text=text[:max_chars]; trunc=True
        else: trunc=len(rows)>max_rows
        base.update({'columns':cols,'rows':vals,'rows_returned':len(vals),'truncated':trunc,'result_chars':len(text)})
    else: raise SystemExit('unsupported_action')
    db.close(); print(json.dumps(base,ensure_ascii=False))
except SystemExit: raise
except Exception as ex: raise SystemExit('sqlite_read_failed: '+type(ex).__name__)
""";

    /// <summary>Bounded PDF text extraction with page markers and provenance.</summary>
    public static AgentTool ReadPdf() => new(
        new ToolSchema("read_pdf",
            "Read bounded text from a workspace PDF with page markers, provenance metadata, size/time limits, and clear encrypted/unsupported errors.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string"},"max_pages":{"type":"integer","minimum":1,"maximum":100,"default":20},
                  "max_chars":{"type":"integer","minimum":500,"maximum":50000,"default":12000},
                  "timeout_seconds":{"type":"integer","minimum":5,"maximum":120,"default":30}},
                 "required":["path"],"additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var path = WorkspacePath(Str(args, "path"));
            if (path is null) return ToolResult.Error("workspace_path_invalid");
            return await RunPythonAsync(ctx, PdfScript,
                [path, Int(args, "max_pages", 20).ToString(), Int(args, "max_chars", 12_000).ToString()],
                Int(args, "timeout_seconds", 30), ct);
        });

    /// <summary>Bounded zip/tar listing or text-entry extraction.</summary>
    public static AgentTool ReadArchive() => new(
        new ToolSchema("read_archive",
            "List or read bounded text entries from a workspace zip/tar archive. Entries are read in memory only; archive traversal cannot write outside the workspace.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string"},"action":{"type":"string","enum":["list","read"],"default":"list"},
                  "entry":{"type":"string"},"max_entries":{"type":"integer","minimum":1,"maximum":500,"default":100},
                  "max_chars":{"type":"integer","minimum":500,"maximum":50000,"default":12000},
                  "timeout_seconds":{"type":"integer","minimum":5,"maximum":120,"default":30}},
                 "required":["path"],"additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var path = WorkspacePath(Str(args, "path"));
            if (path is null) return ToolResult.Error("workspace_path_invalid");
            return await RunPythonAsync(ctx, ArchiveScript,
                [path, Str(args, "action") ?? "list", Str(args, "entry") ?? "",
                 Int(args, "max_entries", 100).ToString(), Int(args, "max_chars", 12_000).ToString()],
                Int(args, "timeout_seconds", 30), ct);
        });

    /// <summary>Read-only bounded SQLite schema/query access.</summary>
    public static AgentTool ReadSqlite() => new(
        new ToolSchema("read_sqlite",
            "Inspect a workspace SQLite database: list tables/schema or execute one read-only bounded SELECT/WITH query with row and byte caps.",
            Schema("""
                {"type":"object","properties":{
                  "path":{"type":"string"},"action":{"type":"string","enum":["tables","query"],"default":"tables"},
                  "sql":{"type":"string"},"max_rows":{"type":"integer","minimum":1,"maximum":500,"default":100},
                  "max_chars":{"type":"integer","minimum":500,"maximum":50000,"default":12000},
                  "timeout_seconds":{"type":"integer","minimum":5,"maximum":120,"default":30}},
                 "required":["path"],"additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var path = WorkspacePath(Str(args, "path"));
            if (path is null) return ToolResult.Error("workspace_path_invalid");
            return await RunPythonAsync(ctx, SqliteScript,
                [path, Str(args, "action") ?? "tables", Str(args, "sql") ?? "",
                 Int(args, "max_rows", 100).ToString(), Int(args, "max_chars", 12_000).ToString()],
                Int(args, "timeout_seconds", 30), ct);
        });
}