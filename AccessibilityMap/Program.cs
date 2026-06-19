using KyivAccessibilityMap.Models;
using KyivAccessibilityMap.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<RoadGraphService>();
builder.Services.AddSingleton<IntersectionService>();
builder.Services.AddSingleton<GraphConnectivityService>();
builder.Services.AddSingleton<RoutingService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

var app = builder.Build();

await EnsureLocalFiles(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseCors("AllowAll");
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

async Task EnsureLocalFiles(WebApplication app)
{
    var env = app.Services.GetRequiredService<IWebHostEnvironment>();
    var config = app.Services.GetRequiredService<IConfiguration>();
    var localPath = Path.Combine(env.ContentRootPath, "data", "kyiv_buildings.json");
    if (File.Exists(localPath)) return;

    var bucketName = config["GoogleCloud:BucketName"];
    var keyPath = Path.Combine(Directory.GetCurrentDirectory(),
                               config["GoogleCloud:KeyFilePath"] ?? "Keys/gcloud-key.json");

    Console.WriteLine("⏳ Завантаження kyiv_buildings.json з GCS...");

    var credential = GetCredential(keyPath);
    var storageClient = await StorageClient.CreateAsync(credential);

    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
    using var fileStream = File.Create(localPath);
    await storageClient.DownloadObjectAsync(bucketName, "data/kyiv_buildings.json", fileStream);

    Console.WriteLine("✅ kyiv_buildings.json завантажено");
}

GoogleCredential GetCredential(string keyPath)
{
    var base64 = Environment.GetEnvironmentVariable("GCLOUD_KEY_JSON");
    if (!string.IsNullOrEmpty(base64))
    {
        var jsonBytes = Convert.FromBase64String(base64);
        var json = System.Text.Encoding.UTF8.GetString(jsonBytes);
        return GoogleCredential.FromJson(json);
    }
    return GoogleCredential.FromFile(keyPath);
}
