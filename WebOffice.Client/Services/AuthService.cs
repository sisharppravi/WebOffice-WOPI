using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace WebOffice.Client.Services;

public class AuthService
{
    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private const string ApiBase = "https://localhost:7130/"; // Адрес API, должен совпадать с настройкой в Program.cs
    private string? _currentUser;

    public AuthService(HttpClient http, IJSRuntime js)
    {
        _http = http;
        _js = js;
    }

    public async Task<(int StatusCode, string Body)> RegisterAsync(RegisterModel model)
    {
        var resp = await _http.PostAsJsonAsync($"{ApiBase}api/auth/register", model);
        var body = await resp.Content.ReadAsStringAsync();
        return ((int)resp.StatusCode, body);
    }

    public async Task<(int StatusCode, string? Body, string? Token)> LoginAsync(LoginModel model)
    {
        var resp = await _http.PostAsJsonAsync($"{ApiBase}api/auth/login", model);
        var body = await resp.Content.ReadAsStringAsync();
        string? token = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body ?? "{}");
            if (doc.RootElement.TryGetProperty("token", out var t)) token = t.GetString();
        }
        catch
        {
        }

        if (!string.IsNullOrEmpty(token))
        {
            await _js.InvokeVoidAsync("localStorage.setItem", "authToken", token);
            // сохраняем логин рядом для удобства (сервер не возвращает имя)
            if (!string.IsNullOrEmpty(model.Login))
            {
                _currentUser = model.Login;
                await _js.InvokeVoidAsync("localStorage.setItem", "authUser", _currentUser);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(model.Login))
            {
                _currentUser = model.Login;
                await _js.InvokeVoidAsync("localStorage.setItem", "authUser", _currentUser);
            }
        }

        return ((int)resp.StatusCode, body, token);
    }

    public async Task<string?> GetCurrentUserAsync()
    {
        if (!string.IsNullOrEmpty(_currentUser)) return _currentUser;
        try
        {
            var user = await _js.InvokeAsync<string>("localStorage.getItem", "authUser");
            if (!string.IsNullOrEmpty(user))
            {
                _currentUser = user;
                return _currentUser;
            }

            var tok = await _js.InvokeAsync<string>("localStorage.getItem", "authToken");
            if (!string.IsNullOrEmpty(tok))
            {
                var name = TryGetNameFromJwt(tok);
                if (!string.IsNullOrEmpty(name))
                {
                    _currentUser = name;
                    await _js.InvokeVoidAsync("localStorage.setItem", "authUser", name);
                    return _currentUser;
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public async Task LogoutAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", "authToken");
            await _js.InvokeVoidAsync("localStorage.removeItem", "authUser");
        }
        catch { }

        _currentUser = null;
    }

    private string? TryGetNameFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var bytes = Convert.FromBase64String(payload);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            string[] claimKeys = new[] { "unique_name", "name", "sub", "username", "login" };
            foreach (var k in claimKeys)
            {
                if (root.TryGetProperty(k, out var v)) return v.GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // Модели используемые клиентом (повторяемые другими файлами)
    public class RegisterModel
    {
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class LoginModel
    {
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
