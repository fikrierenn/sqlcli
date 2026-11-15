-- ============================================================
-- Salt-okuma yetkisi: bir kullanıcıya "her şeyi OKUMA" (yazma YOK)
-- Çalıştıran: sysadmin / securityadmin (DBA). bkmrapor bunu kendine veremez.
-- Neden: sqlcli MCP'nin gerçek güvenlik sınırı veritabanı yetkisidir.
-- ============================================================

-- ---- AYAR: yalnızca bu iki satırı değiştir -------------------
DECLARE @Kullanici sysname = N'bkmrapor';        -- yetki verilecek SQL login
DECLARE @Veritabani sysname = N'*';              -- tüm veritabanları (tek veritabanı için adını yaz)
-- --------------------------------------------------------------

SET NOCOUNT ON;

-- 1) Login yoksa uyar (oluşturma bu betikte YOK; şifre betiğe yazılmaz)
IF SUSER_ID(@Kullanici) IS NULL
BEGIN
    RAISERROR(N'Login bulunamadı: %s. Önce login oluşturulmalı (şifreyi DBA girer).', 16, 1, @Kullanici);
    RETURN;
END

-- 2) Hedef veritabanları (sistem veritabanları hariç)
DECLARE @hedef TABLE (ad sysname);
INSERT @hedef
SELECT name FROM sys.databases
WHERE state_desc = 'ONLINE'
  AND database_id > 4
  AND (@Veritabani = N'*' OR name = @Veritabani);

IF NOT EXISTS (SELECT 1 FROM @hedef)
BEGIN
    RAISERROR(N'Hedef veritabanı bulunamadı veya çevrimdışı.', 16, 1);
    RETURN;
END

-- 3) Her veritabanında: kullanıcıyı oluştur + yalnız okuma rolü + şema görme
-- (EXEC (...) içinde QUOTENAME çağrılamaz: tırnaklı adı önce değişkene alıyoruz.)
DECLARE @q nvarchar(300) = QUOTENAME(@Kullanici);
DECLARE @db sysname, @sql nvarchar(max);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT ad FROM @hedef;
OPEN c; FETCH NEXT FROM c INTO @db;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'USE ' + QUOTENAME(@db) + N';
    IF DATABASE_PRINCIPAL_ID(N''' + REPLACE(@Kullanici, N'''', N'''''') + N''') IS NULL
        CREATE USER ' + @q + N' FOR LOGIN ' + @q + N';
    ALTER ROLE db_datareader ADD MEMBER ' + @q + N';
    -- Tablo/kolon yapısını görmek için (describe_table, search_schema); veri yazmaz:
    GRANT VIEW DEFINITION TO ' + @q + N';
    -- NOT: DENY eklenmedi; kullanıcı başka projelerde de kullanılıyorsa onları bozabilir.
    PRINT N''Tamam: '' + DB_NAME();';
    BEGIN TRY
        EXEC sys.sp_executesql @sql;
    END TRY
    BEGIN CATCH
        -- Bir veritabanı (ör. salt-okunur kopya) hata verirse diğerleri devam etsin.
        PRINT N'ATLANDI: ' + @db + N' -> ' + ERROR_MESSAGE();
    END CATCH
    FETCH NEXT FROM c INTO @db;
END
CLOSE c; DEALLOCATE c;

-- 4) KONTROL: hepsi 0 olmalı (yazma yetkisi YOK), okuma 1
SELECT  @Kullanici AS kullanici,
        IS_SRVROLEMEMBER('sysadmin', @Kullanici) AS sysadmin;

-- Her veritabanı için elle kontrol (EncoreMerkez örneği):
-- USE EncoreMerkez;
-- SELECT IS_ROLEMEMBER('db_datareader','bkmrapor') AS okuma,
--        IS_ROLEMEMBER('db_datawriter','bkmrapor') AS yazma,
--        IS_ROLEMEMBER('db_owner','bkmrapor')      AS sahip;

-- ---- GERİ ALMA --------------------------------------------------
-- USE EncoreMerkez;
-- ALTER ROLE db_datareader DROP MEMBER bkmrapor;
-- REVOKE VIEW DEFINITION FROM bkmrapor;
-- DROP USER bkmrapor;
