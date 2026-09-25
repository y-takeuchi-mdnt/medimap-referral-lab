using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using MedimapReferralLab;
using MedimapReferralLab.Components;
using MedimapReferralLab.Core.Ai;
using MedimapReferralLab.Core.Cases;
using MedimapReferralLab.Core.Catalog;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.WebEncoders;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 日本語を数値文字参照にしない（HTMLを読みやすくする）
builder.Services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

// 使える人を絞る（12_テスト版の仕様.md 5章「省かないもの」）。
// 試作は Agent の DB を使わないので、設定ファイルに書いた利用者だけがログインできるようにする。
builder.Services.Configure<LabAuthOptions>(builder.Configuration.GetSection(LabAuthOptions.SectionName));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// 逆紹介検索（Lab.Core）
var aiOptions = builder.Configuration.GetSection(AzureOpenAiOptions.SectionName).Get<AzureOpenAiOptions>() ?? new();
var searchOptions = builder.Configuration.GetSection(ReferralSearchOptions.SectionName).Get<ReferralSearchOptions>() ?? new();
var catalogDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, searchOptions.CatalogDirectory));
var catalog = SearchKeyCatalog.LoadFromDirectory(catalogDirectory);
var cities = CityCatalog.LoadFromDirectory(catalogDirectory);

builder.Services.AddSingleton(aiOptions);
builder.Services.AddSingleton(searchOptions);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(cities);
builder.Services.AddSingleton(new PromptBuilder(catalog, cities)); // 静的プロンプトは起動時に1回だけ作る
builder.Services.AddSingleton<OutputValidator>();
builder.Services.AddHttpClient<ReferralAiClient>(client => client.Timeout = Timeout.InfiniteTimeSpan); // タイムアウトは ReferralAiClient が持つ
builder.Services.AddTransient<ReferralDraftService>();
builder.Services.AddSingleton<IReadOnlyList<FictionalCase>>(_ =>
{
    var path = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "data", "cases", FictionalCase.DefaultFileName));
    return File.Exists(path) ? FictionalCase.Load(path) : [];
});

var app = builder.Build();

if (searchOptions.LogPrompts)
{
    app.Logger.LogWarning("ReferralSearch:LogPrompts=true。送ったプロンプト（自由文を含む）をログに出します。架空の症例だけで使い、確かめ終わったら false に戻してください。");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapPost("/account/login", async (HttpContext context, [FromForm] string userName, [FromForm] string password,
    [FromForm] string? returnUrl, Microsoft.Extensions.Options.IOptions<LabAuthOptions> auth) =>
{
    var user = auth.Value.Users.FirstOrDefault(u => u.UserName == userName);
    if (user is null || string.IsNullOrEmpty(user.Password) || !FixedTimeEquals(user.Password, password))
        return Results.LocalRedirect($"/login?failed=1&returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}");

    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.UserName)], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.LocalRedirect(returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/");
});

app.MapPost("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

app.Run();

static bool FixedTimeEquals(string expected, string actual) =>
    CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
        SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
