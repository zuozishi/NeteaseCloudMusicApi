using System;
using System.Collections.Generic;
using System.Extensions;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NeteaseCloudMusicApi.Utils;

namespace NeteaseCloudMusicApi {
	/// <summary>
	/// 网易云音乐API
	/// </summary>
	public sealed partial class CloudMusicApi {
		/// <summary>
		/// Cookies
		/// </summary>
		public CookieCollection Cookies { get; }

		/// <summary>
		/// 请求头中的 X-Real-IP，如果为 <see langword="null"/> 则不设置
		/// </summary>
		public string RealIP { get; set; }

		/// <summary>
		/// 是否使用代理
		/// </summary>
		public bool UseProxy { get; set; }

		/// <summary>
		/// 代理
		/// </summary>
		public IWebProxy Proxy { get; set; }

		/// <summary>
		/// 构造器
		/// </summary>
		public CloudMusicApi() {
			Cookies = new CookieCollection();
			UseProxy = true;
		}

		/// <summary>
		/// 构造器
		/// </summary>
		/// <param name="cookies"></param>
		public CloudMusicApi(CookieCollection cookies) {
			if (cookies is null)
				throw new ArgumentNullException(nameof(cookies));

			Cookies = new CookieCollection { cookies };
		}

		/// <summary>
		/// 构造器
		/// </summary>
		/// <param name="cookies"></param>
		public CloudMusicApi(IEnumerable<Cookie> cookies) {
			if (cookies is null)
				throw new ArgumentNullException(nameof(cookies));

			Cookies = new CookieCollection();
			foreach (var cookie in cookies)
				Cookies.Add(cookie);
		}

		/// <summary>
		/// API请求
		/// </summary>
		/// <param name="provider">API提供者</param>
		/// <param name="queries">参数</param>
		/// <param name="throwIfFailed">如果请求失败，抛出异常</param>
		/// <returns></returns>
		public async Task<JsonNode> RequestAsync(CloudMusicApiProvider provider, Dictionary<string, object> queries = null, bool throwIfFailed = true) {
			if (provider is null)
				throw new ArgumentNullException(nameof(provider));

			if (queries is null)
				queries = new Dictionary<string, object>();
			JsonNode json;
			if (provider == CloudMusicApiProviders.CheckMusic)
				json = await HandleCheckMusicAsync(queries);
			else if (provider == CloudMusicApiProviders.Login)
				json = await HandleLoginAsync(queries);
			else if (provider == CloudMusicApiProviders.LoginStatus)
				json = await HandleLoginStatusAsync();
			else if (provider == CloudMusicApiProviders.RelatedPlaylist)
				json = await HandleRelatedPlaylistAsync(queries);
			else
				json = await RequestAsync(provider.Method, provider.Url(queries), provider.Data(queries), provider.Options);
			if (throwIfFailed && !IsSuccess(json))
				throw new HttpRequestException($"调用 '{provider.Route}' 失败");
			return json;
		}

		/// <summary>
		/// API是否请求成功
		/// </summary>
		/// <param name="json">服务器返回的数据</param>
		/// <returns></returns>
		public static bool IsSuccess(JsonNode json) {
			if (json is null)
				throw new ArgumentNullException(nameof(json));

			int code = (int)json["code"];
			return 200 <= code && code <= 299;
		}

		private async Task<JsonNode> RequestAsync(HttpMethod method, string url, Dictionary<string, object> data, Options options) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));
			if (url is null)
				throw new ArgumentNullException(nameof(url));
			if (data is null)
				throw new ArgumentNullException(nameof(data));
			if (options is null)
				throw new ArgumentNullException(nameof(options));

			var json = await Request.CreateRequest(method.Method, url, data, MergeOptions(options), Cookies);
			if ((int)json["code"] == 301)
				json["msg"] = "未登录";
			return json;
		}

		private Options MergeOptions(Options options) {
			var newOptions = new Options {
				Crypto = options.Crypto,
				Cookie = new CookieCollection(),
				UA = options.UA,
				Url = options.Url,
				RealIP = RealIP,
				UseProxy = UseProxy,
				Proxy = Proxy
			};
			newOptions.Cookie.Add(options.Cookie);
			newOptions.Cookie.Add(Cookies);
			return newOptions;
		}

		private async Task<JsonNode> HandleCheckMusicAsync(Dictionary<string, object> queries) {
			var provider = CloudMusicApiProviders.CheckMusic;
			var json = await RequestAsync(provider.Method, provider.Url(queries), provider.Data(queries), provider.Options);
			bool playable = (int)json["code"] == 200 && (int)json["data"][0]["code"] == 200;
			var result = new JsonObject {
				["success"] = playable,
				["message"] = playable ? "ok" : "亲爱的,暂无版权"
			};
			return result;
		}

		private async Task<JsonNode> HandleLoginAsync(Dictionary<string, object> queries) {
			var provider = CloudMusicApiProviders.Login;
			var json = await RequestAsync(provider.Method, provider.Url(queries), provider.Data(queries), provider.Options);
			if ((int)json["code"] == 502) {
				json = new JsonObject {
					["code"] = 502,
					["message"] = "账号或密码错误"
				};
			}
			return json;
		}

		private async Task<JsonNode> HandleLoginStatusAsync() {
			try {
				const string GUSER = "GUser=";
				const string GBINDS = "GBinds=";

				byte[] data = await QuickHttp.SendAsync("https://music.163.com", "Get", $"Cookie: {QuickHttp.ToCookieHeader(Cookies)}");
				string s = Encoding.UTF8.GetString(data);
				int index = s.IndexOf(GUSER, StringComparison.Ordinal);
				if (index == -1)
					return new JsonObject { ["code"] = 301 };
				var json = new JsonObject { ["code"] = 200 };
				string text = s[(index + GUSER.Length)..];
				index = text.IndexOf("};");
				text = text[0..(index + 1)];
				var itemArray = new List<string>(new string[] {
					"userId", "nickname","avatarUrl","birthday","userType","djStatus"
				});
				itemArray.ForEach(x => text = text.Replace(x, "\"" + x + "\""));
				json.Add("profile", JsonNode.Parse(text));
				index = s.IndexOf(GBINDS, StringComparison.Ordinal);
				if (index == -1)
					return new JsonObject { ["code"] = 301 };
				text = s[(index + GBINDS.Length)..];
				index = text.IndexOf("];");
				text = text[..(index + 1)];
				json.Add("bindings", JsonNode.Parse(text));
				return json;
			}
			catch{
				return new JsonObject { ["code"] = 301 };
			}
		}

		private async Task<JsonNode> HandleRelatedPlaylistAsync(Dictionary<string, object> queries) {
			try {
				byte[] data = await QuickHttp.SendAsync($"https://music.163.com/playlist?id={queries["id"]}", "Get", $"User-Agent: {Request.ChooseUserAgent("pc")}");
				string s = Encoding.UTF8.GetString(data);
				var matchs = Regex.Matches(s, @"<div class=""cver u-cover u-cover-3"">[\s\S]*?<img src=""([^""]+)"">[\s\S]*?<a class=""sname f-fs1 s-fc0"" href=""([^""]+)""[^>]*>([^<]+?)<\/a>[\s\S]*?<a class=""nm nm f-thide s-fc3"" href=""([^""]+)""[^>]*>([^<]+?)<\/a>");
				var playlists = new JsonArray(matchs.Cast<Match>().Select(match => new JsonObject {
					["creator"] = new JsonObject {
						["userId"] = match.Groups[4].Value.Substring("/user/home?id=".Length),
						["nickname"] = match.Groups[5].Value
					},
					["coverImgUrl"] = match.Groups[1].Value.Substring(0, match.Groups[1].Value.Length - "?param=50y50".Length),
					["name"] = match.Groups[3].Value,
					["id"] = match.Groups[2].Value.Substring("/playlist?id=".Length),
				}).ToArray());
				return new JsonObject {
					["code"] = 200,
					["playlists"] = playlists
				};
			}
			catch (Exception ex) {
				return new JsonObject {
					["code"] = 500,
					["msg"] = ex.ToFullString()
				};
			}
		}
	}
}
