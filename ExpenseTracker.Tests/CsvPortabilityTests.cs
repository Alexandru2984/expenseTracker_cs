using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Infrastructure;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class CsvPortabilityTests(TestApiFactory factory)
{
    private static Task<HttpResponseMessage> Import(HttpClient client, string csv)
    {
        var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "portable.csv");
        return client.PostAsync("/api/subscriptions/import", form);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public async Task Distinct_source_ids_preserve_both_identical_and_different_plans_with_the_same_name(decimal otherCost)
    {
        var (source, _) = await TestAccount.Create(factory);
        foreach (var cost in new[] { 10m, otherCost })
            (await source.PostAsJsonAsync("/api/subscriptions", new { name = "Shared plan", cost, currency = "EUR", nextBillingDate = "2027-01-01" })).EnsureSuccessStatusCode();
        var csv = await source.GetStringAsync("/api/subscriptions/export");
        var (target, _) = await TestAccount.Create(factory);
        var imported = await Import(target, csv); imported.EnsureSuccessStatusCode();
        Assert.Equal(2, (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32());
        var repeated = await Import(target, csv); repeated.EnsureSuccessStatusCode();
        var result = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, result.GetProperty("imported").GetInt32()); Assert.Equal(2, result.GetProperty("skipped").GetInt32());
        var ownExport = await target.GetStringAsync("/api/subscriptions/export");
        var ownRepeated = await Import(target, ownExport); ownRepeated.EnsureSuccessStatusCode();
        Assert.Equal(0, (await ownRepeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32());
    }

    [Fact]
    public async Task Portable_formula_escaping_preserves_original_names_and_leading_apostrophes_in_notes()
    {
        var (source, _) = await TestAccount.Create(factory);
        var name = "=SUM(1,2)"; var notes = "'Original quote\n=Not executable";
        (await source.PostAsJsonAsync("/api/subscriptions", new { name, cost = 10, nextBillingDate = "2027-01-01", details = new { notes } })).EnsureSuccessStatusCode();
        var csv = await source.GetStringAsync("/api/subscriptions/export");
        Assert.Contains("\"'=SUM(1,2)\"", csv);
        var (target, _) = await TestAccount.Create(factory);
        (await Import(target, csv)).EnsureSuccessStatusCode();
        var list = await target.GetFromJsonAsync<JsonElement>("/api/subscriptions");
        Assert.Equal(name, list.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(notes, list.GetProperty("items")[0].GetProperty("details").GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Legacy_rows_compare_full_content_and_do_not_strip_original_apostrophes()
    {
        var (client, _) = await TestAccount.Create(factory);
        var csv = "Name,Cost,NextBillingDate,Currency,Notes\nSame,10,2027-01-01,EUR,'Original\nSame,20,2027-01-01,EUR,Other\nSame,10,2027-01-01,EUR,'Original";
        var response = await Import(client, csv); response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("imported").GetInt32()); Assert.Equal(1, result.GetProperty("skipped").GetInt32());
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions?sortBy=cost");
        Assert.Equal("'Original", saved.GetProperty("items")[0].GetProperty("details").GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Import_source_identity_does_not_read_or_overwrite_another_accounts_entity()
    {
        var (owner, originalOwner) = await TestAccount.Create(factory);
        var original = await owner.PostAsJsonAsync("/api/subscriptions", new { name = "Private original", cost = 99, nextBillingDate = "2027-01-01" });
        var id = (await original.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (target, targetOwner) = await TestAccount.Create(factory);
        var response = await Import(target, $"Id,Name,Cost,NextBillingDate\n{id},Own copy,10,2027-01-01"); response.EnsureSuccessStatusCode();
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unchanged = await db.Subscriptions.SingleAsync(s => s.Id == id);
        Assert.Equal(originalOwner, unchanged.UserId); Assert.Equal(99, unchanged.Cost);
        var copy = await db.Subscriptions.SingleAsync(s => s.UserId == targetOwner);
        Assert.NotEqual(id, copy.Id); Assert.Equal(id, copy.ImportSourceId); Assert.Equal("Own copy", copy.Name);
    }

    [Theory]
    [InlineData("not-a-guid", "2")]
    [InlineData("00000000-0000-0000-0000-000000000000", "2")]
    [InlineData("", "999")]
    public async Task Malformed_source_ids_and_unknown_formats_are_reported_without_writing(string id, string version)
    {
        var (client, owner) = await TestAccount.Create(factory);
        var response = await Import(client, $"Id,Name,Cost,NextBillingDate,FormatVersion\n{id},Bad,10,2027-01-01,{version}");
        response.EnsureSuccessStatusCode();
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rejected").GetInt32());
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions.AnyAsync(s => s.UserId == owner));
    }

    [Fact]
    public async Task Paged_export_matches_filtered_list_order_and_caps_the_file_to_fifty_records()
    {
        var (client, owner) = await TestAccount.Create(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.AddRange(Enumerable.Range(0, 70).Select(i => new SubscriptionItem
                { UserId = owner, Name = "Plan " + i, Cost = 10, Currency = "EUR", NextBillingDate = new(2027, 1, 1), Notes = "Keyword" }));
            await db.SaveChangesAsync();
        }
        var list = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions?skip=10&take=50&search=Keyword&sortBy=cost");
        var csv = await client.GetStringAsync("/api/subscriptions/export?pageOnly=true&skip=10&take=999&search=Keyword&sortBy=cost");
        var rows = Csv.Parse(csv); Assert.Equal(51, rows.Count);
        Assert.Equal(list.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetString()), rows.Skip(1).Select(row => row[0]));
        Assert.True(Encoding.UTF8.GetByteCount(csv) <= 1_000_000);
    }

    [Fact]
    public async Task Oversized_complete_export_offers_a_bounded_page_that_can_be_imported()
    {
        var (client, owner) = await TestAccount.Create(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.AddRange(Enumerable.Range(0, 200).Select(i => new SubscriptionItem
                { UserId = owner, Name = "Large " + i, NextBillingDate = new(2027, 1, 1), Notes = new string('字', 2000) }));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.GetAsync("/api/subscriptions/export")).StatusCode);
        var page = await client.GetStringAsync("/api/subscriptions/export?pageOnly=true&take=50");
        Assert.True(Encoding.UTF8.GetByteCount(page) <= 1_000_000);
        var (target, _) = await TestAccount.Create(factory); var response = await Import(target, page); response.EnsureSuccessStatusCode();
        Assert.Equal(50, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32());
    }

    [PostgresFact]
    public async Task Concurrent_imports_of_the_same_source_are_idempotent()
    {
        var (client, owner) = await TestAccount.Create(factory); var source = Guid.NewGuid();
        var csv = $"Id,Name,Cost,NextBillingDate\n{source},Concurrent,10,2027-01-01";
        var responses = await Task.WhenAll(Import(client, csv), Import(client, csv));
        var imported = 0;
        foreach (var response in responses)
        { response.EnsureSuccessStatusCode(); imported += (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32(); }
        Assert.Equal(1, imported);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions.CountAsync(s => s.UserId == owner));
    }

    [PostgresFact]
    public async Task Concurrent_create_and_import_cannot_exceed_the_account_capacity()
    {
        var (client, owner) = await TestAccount.Create(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.AddRange(Enumerable.Range(0, SubscriptionCapacity.Maximum - 1).Select(i => new SubscriptionItem
                { UserId = owner, Name = "Existing " + i, NextBillingDate = new(2027, 1, 1) }));
            await db.SaveChangesAsync();
        }
        var create = client.PostAsJsonAsync("/api/subscriptions", new { name = "Last direct", cost = 10, nextBillingDate = "2027-01-01" });
        var import = Import(client, $"Id,Name,Cost,NextBillingDate\n{Guid.NewGuid()},Last imported,10,2027-01-01\n{Guid.NewGuid()},Over capacity,10,2027-01-01");
        await Task.WhenAll(create, import);
        Assert.Contains(create.Result.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
        import.Result.EnsureSuccessStatusCode();
        using var check = factory.Services.CreateScope();
        Assert.Equal(SubscriptionCapacity.Maximum, await check.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions.CountAsync(s => s.UserId == owner));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/subscriptions", new { name = "Too many", cost = 1, nextBillingDate = "2027-01-01" })).StatusCode);
    }
}
