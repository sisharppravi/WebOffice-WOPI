using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WebOffice.Client;
using WebOffice.Client.Services;   // ← для AuthService

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// === ГЛАВНОЕ ИСПРАВЛЕНИЕ: HttpClient теперь указывает на твой API ===
builder.Services.AddScoped(sp => new HttpClient 
{ 
    BaseAddress = new Uri("https://localhost:7130")   // ← поменяй порт, если у тебя другой (смотри launchSettings.json в WebOffice.Api)
});

builder.Services.AddScoped<AuthService>();

await builder.Build().RunAsync();