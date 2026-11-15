using SqlCli.Core;

namespace SqlCli.Tests;

// Sema araclari: yukleme (sozluk + liste), Turkce-duyarsiz arama, bayatlik (decay), aday deposu.
public class SemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlcli-sema-" + Guid.NewGuid().ToString("N"));

    public SemaTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "entities.yaml"), """
            version: 1
            ai_hints:
              - "yyyy-MM-dd tarih formatı kullanılmaz."
              - "urn.stkKod BARKOD DEĞİL."
            databases:
              EncoreMerkez: {role: "POS", compat: {unavailable: [TRIM]}}
            entities:
              EncoreMerkez.dbo.SalesProducts:
                grain: "kalem"
                note: "İade ve indirim kalem düzeyinde"
                confidence: 0.95
                last_verified: 2026-10-01
              urn:
                note: "ürün ana tablosu urnKtgr2ID kategori"
                confidence: 1.0
            """);
        File.WriteAllText(Path.Combine(_dir, "bridges.yaml"), """
            version: 1
            bridges:
              - id: products-code-stkid
                from: Products.Code
                to: urn.stkID
                scope: encoremerkez
                note: "Barkod değil stkID ile eşleşir"
                confidence: 0.9
                last_verified: 2026-01-01
                uses: ["metrics:net_ciro", "bridges:yok-boyle-kayit"]
            """);
        File.WriteAllText(Path.Combine(_dir, "metrics.yaml"), """
            version: 1
            metrics:
              net_ciro:
                title: "Net ciro (KDV hariç)"
                confidence: 0.8
                last_verified: 2026-10-10
            """);
        File.WriteAllText(Path.Combine(_dir, "queries.yaml"), """
            version: 1
            queries:
              - id: aylik-net-ciro
                question: "Mağaza bazında aylık net ciro"
                mcp_safe: true
                verified_sql: |
                  SELECT 1
                last_verified: 2026-10-10
            """);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Load_dict_and_list_formats_and_ai_hints()
    {
        var idx = SemaIndex.Open(_dir);
        Assert.Equal(5, idx.Records.Count);
        Assert.Contains(idx.Records, r => r.Kind == "entity" && r.Id == "EncoreMerkez.dbo.SalesProducts");
        Assert.Contains(idx.Records, r => r.Kind == "bridge" && r.Id == "products-code-stkid");
        Assert.Contains(idx.Records, r => r.Kind == "metric" && r.Id == "net_ciro");
        Assert.Equal(2, idx.AiHints.Count);
        Assert.NotNull(idx.Databases?["EncoreMerkez"]);
        Assert.Empty(idx.LoadErrors);
    }

    [Theory]
    [InlineData("İADE", "EncoreMerkez.dbo.SalesProducts")]       // büyük İ → i
    [InlineData("iade indirim", "EncoreMerkez.dbo.SalesProducts")]
    [InlineData("urnKtgr2ID", "urn")]                              // tanımlayıcı alt dizge
    [InlineData("kategori", "urn")]
    public void Search_turkish_insensitive(string query, string beklenenId)
    {
        var idx = SemaIndex.Open(_dir);
        var hits = idx.Search(query, null, 5);
        Assert.NotEmpty(hits);
        Assert.Equal(beklenenId, hits[0].Record.Id);
    }

    [Fact]
    public void Search_kind_filter_and_empty_result()
    {
        var idx = SemaIndex.Open(_dir);
        Assert.All(idx.Search("ciro", "metric", 10), h => Assert.Equal("metric", h.Record.Kind));
        Assert.Empty(idx.Search("hicbirseybulunamaz", null, 5));
        Assert.Throws<ArgumentException>(() => SemaIndex.NormalizeKind("uydurma"));
    }

    [Fact]
    public void Question_suffix_body_match_finds_verified_query()
    {
        var idx = SemaIndex.Open(_dir);
        var hits = idx.Search("aylık cirosu nedir", "query", 3);   // "cirosu" → "ciro"
        Assert.Single(hits);
        Assert.Equal("aylik-net-ciro", hits[0].Record.Id);
    }

    [Fact]
    public void Get_case_and_turkish_insensitive()
    {
        var idx = SemaIndex.Open(_dir);
        Assert.NotNull(idx.Get(null, "encoremerkez.dbo.salesproducts"));
        Assert.NotNull(idx.Get("bridge", "PRODUCTS-CODE-STKID"));
        Assert.Null(idx.Get("metric", "products-code-stkid"));
    }

    [Fact]
    public void Uses_cross_references()
    {
        var idx = SemaIndex.Open(_dir);
        var uses = idx.Get("bridge", "products-code-stkid")!.Uses().ToList();
        Assert.Equal(2, uses.Count);
        Assert.Equal(("metric", "net_ciro"), uses[0]);
        Assert.NotNull(idx.Get(uses[0].Kind, uses[0].Id));
        Assert.Null(idx.Get(uses[1].Kind, uses[1].Id));
    }

    [Fact]
    public void Staleness_decay_rule()
    {
        var idx = SemaIndex.Open(_dir);
        var todayDate = new DateOnly(2026, 11, 15);
        // confidence 1.0 → muaf
        Assert.Equal("permanent", idx.Get("entity", "urn")!.Freshness(todayDate));
        // 0.9 + son doğrulama 2026-01-01 → 180 gün doldu → BAYAT
        Assert.StartsWith("STALE", idx.Get("bridge", "products-code-stkid")!.Freshness(todayDate));
        // 0.8 + 2026-10-10 → 90 gün içinde → taze
        Assert.Equal("fresh", idx.Get("metric", "net_ciro")!.Freshness(todayDate));
        // 0.95 + 2026-10-01 → 180 gün içinde → taze
        Assert.Equal("fresh", idx.Get("entity", "EncoreMerkez.dbo.SalesProducts")!.Freshness(todayDate));
        // aynı kayıt çok ileri bir tarihte bayat
        Assert.StartsWith("STALE", idx.Get("metric", "net_ciro")!.Freshness(new DateOnly(2027, 3, 1)));
    }

    [Fact]
    public void Startup_missing_folder_or_schema_file_errors()
    {
        Assert.Throws<DirectoryNotFoundException>(() => SemaIndex.Open(Path.Combine(_dir, "yok")));
        var missingDir = Directory.CreateDirectory(Path.Combine(_dir, "bos")).FullName;
        Assert.Throws<FileNotFoundException>(() => SemaIndex.Open(missingDir));
    }

    [Fact]
    public void Broken_yaml_reported_as_error_others_load()
    {
        File.WriteAllText(Path.Combine(_dir, "codes.yaml"), "codes: [\n  - {unclosed");
        var idx = SemaIndex.Open(_dir);
        Assert.Equal(5, idx.Records.Count);
        Assert.Contains(idx.LoadErrors, e => e.StartsWith("codes.yaml"));
    }

    [Fact]
    public void File_reloaded_when_changed()
    {
        var idx = SemaIndex.Open(_dir);
        Assert.Equal(5, idx.Records.Count);
        var filePath = Path.Combine(_dir, "metrics.yaml");
        File.AppendAllText(filePath, "\n  yeni_metrik:\n    title: \"Yeni\"\n    confidence: 0.5\n");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(6, idx.Records.Count);
    }

    // ── Aday deposu ──────────────────────────────────────────────────────

    private string CandidatesPath() => Path.Combine(_dir, "adaylar.jsonl");

    [Fact]
    public void Candidate_add_cap_and_fields()
    {
        SemaCandidates.ResetSessionCounter();
        var filePath = CandidatesPath();
        var (id, dup) = SemaCandidates.Add("bridges", "Products.Code urn.stkID ile eşleşir", "SELECT ... 0 yetim satır",
            0.99, ["bridges:x"], "erp", filePath);
        Assert.False(dup);
        Assert.StartsWith($"aday-{DateTime.Now:yyyyMMdd}-001", id);
        var line = Assert.Single(SemaCandidates.ReadAll(filePath));
        Assert.Equal(0.8, (double)line["confidence"]!);          // tavan
        Assert.Equal("aday", line["durum"]!.ToString());
        Assert.Equal("erp", line["profil"]!.ToString());
        Assert.Equal("sqlcli-mcp", line["kaynak_proje"]!.ToString());
    }

    [Fact]
    public void Candidate_same_fact_not_added_twice()
    {
        SemaCandidates.ResetSessionCounter();
        var filePath = CandidatesPath();
        var (id1, _) = SemaCandidates.Add("codes", "ehTip 4 = alış faturası", "SELECT ehTip, COUNT(*) ... sonuç", null, null, null, filePath);
        var (id2, dup) = SemaCandidates.Add("codes", "EHTİP 4 = ALIŞ FATURASI", "başka kanıt metni burada", null, null, null, filePath);
        Assert.True(dup);
        Assert.Equal(id1, id2);
        Assert.Single(SemaCandidates.ReadAll(filePath));
    }

    [Theory]
    [InlineData("yok", "yeterince uzun bir gerçek", "yeterince uzun kanıt")]       // geçersiz hedef
    [InlineData("bridges", "kısa", "yeterince uzun kanıt")]                       // gerçek çok kısa
    [InlineData("bridges", "yeterince uzun bir gerçek", "kısa")]                  // kanıt çok kısa
    public void Candidate_validation_rejected(string target, string real, string proof)
    {
        SemaCandidates.ResetSessionCounter();
        Assert.Throws<ArgumentException>(() => SemaCandidates.Add(target, real, proof, null, null, null, CandidatesPath()));
        Assert.False(File.Exists(CandidatesPath()));
    }

    [Fact]
    public void Candidate_text_cannot_break_file_format_and_bad_line_skipped()
    {
        SemaCandidates.ResetSessionCounter();
        var filePath = CandidatesPath();
        SemaCandidates.Add("entities", "satır\nsonu ve \"tırnak\" içeren gerçek", "kanıt\r\nçok satırlı metin", null, null, null, filePath);
        File.AppendAllText(filePath, "{bozuk json satiri\n");
        SemaCandidates.Add("metrics", "ikinci bağımsız gerçek metni", "ikinci kanıt metni burada", null, null, null, filePath);

        Assert.Equal(3, File.ReadAllLines(filePath).Length);            // her kayıt tam bir satır (+1 bozuk)
        var everything = SemaCandidates.ReadAll(filePath);
        Assert.Equal(2, everything.Count);
        Assert.Contains("\n", everything[0]["gercek"]!.ToString());      // yeni satır içerikte korunur
    }

    [Fact]
    public void Candidate_status_update_line_overrides_fields()
    {
        SemaCandidates.ResetSessionCounter();
        var filePath = CandidatesPath();
        var (id, _) = SemaCandidates.Add("bridges", "bir gerçek bir gerçek", "bir kanıt bir kanıt", null, null, null, filePath);
        File.AppendAllText(filePath, $"{{\"id\":\"{id}\",\"durum\":\"alindi (bridges:x)\"}}\n");
        var tek = Assert.Single(SemaCandidates.ReadAll(filePath));
        Assert.StartsWith("alindi", tek["durum"]!.ToString());
        Assert.Equal("bir gerçek bir gerçek", tek["gercek"]!.ToString());
    }

    [Fact]
    public void Candidate_session_limit()
    {
        SemaCandidates.ResetSessionCounter();
        var filePath = CandidatesPath();
        for (int i = 0; i < SemaCandidates.MaxPerSession; i++)
            SemaCandidates.Add("codes", $"benzersiz gerçek numara {i}", $"benzersiz kanıt numara {i}", null, null, null, filePath);
        Assert.Throws<InvalidOperationException>(() =>
            SemaCandidates.Add("codes", "bir tane daha gerçek", "bir tane daha kanıt", null, null, null, filePath));
        Assert.Equal(SemaCandidates.MaxPerSession, SemaCandidates.ReadAll(filePath).Count);
    }

    [Fact]
    public void Candidate_concurrent_add_ids_unique()
    {
        SemaCandidates.ResetSessionCounter();
        var filePath = CandidatesPath();
        Parallel.For(0, 12, i =>
            SemaCandidates.Add("metrics", $"eşzamanlı gerçek {i}", $"eşzamanlı kanıt {i}", null, null, null, filePath));
        var everything = SemaCandidates.ReadAll(filePath);
        Assert.Equal(12, everything.Count);
        Assert.Equal(12, everything.Select(h => h["id"]!.ToString()).Distinct().Count());
    }

    [Fact]
    public void Candidate_appears_in_search_with_unverified_label()
    {
        SemaCandidates.ResetSessionCounter();
        var filePath = CandidatesPath();
        SemaCandidates.Add("bridges", "hediye çeki iadesi ayrı tabloda tutulur", "SELECT ... iade sonucu 12 satır", null, null, null, filePath);
        var idx = SemaIndex.Open(_dir);
        var extra = SemaCandidates.ReadAll(filePath).Select(SemaCandidates.ToRecord).ToList();
        var hit = Assert.Single(idx.Search("hediye çeki", null, 5, null, extra));
        Assert.Equal("candidate", hit.Record.Kind);
        Assert.Equal("UNVERIFIED CANDIDATE", hit.Record.Freshness(new DateOnly(2026, 11, 15)));
    }
}
