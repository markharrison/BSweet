using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OAuth;
using Microsoft.Extensions.Configuration;
using HtmlAgilityPack;
using static System.Net.WebRequestMethods;

namespace BSweetConsole
{
    class Program
    {
        static string username = "";
        static string password = "";

        static async Task<string> GetTwitterImageFromUrl(string url)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    string htmlContent = await client.GetStringAsync(url);

                    var htmlDoc = new HtmlDocument();
                    htmlDoc.LoadHtml(htmlContent);

                    var twitterImageMetaTag = htmlDoc.DocumentNode.SelectSingleNode("//meta[@name='twitter:image']");
                    if (twitterImageMetaTag != null)
                    {
                        return twitterImageMetaTag.GetAttributeValue("content", null);
                    }

                    var twitterImageSrcMetaTag = htmlDoc.DocumentNode.SelectSingleNode("//meta[@name='twitter:image:src']");
                    if (twitterImageSrcMetaTag != null)
                    {
                        return twitterImageSrcMetaTag.GetAttributeValue("content", null);
                    }

                    var ogImageMetaTag = htmlDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
                    if (ogImageMetaTag != null)
                    {
                        return ogImageMetaTag.GetAttributeValue("content", null);
                    }

                    var ogImageSecureUrlMetaTag = htmlDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image:secure_url']");
                    if (ogImageSecureUrlMetaTag != null)
                    {
                        return ogImageSecureUrlMetaTag.GetAttributeValue("content", null);
                    }

                    var ogImageUrlMetaTag = htmlDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image:url']");
                    if (ogImageUrlMetaTag != null)
                    {
                        return ogImageUrlMetaTag.GetAttributeValue("content", null);
                    }

                    var linkImageSrcTag = htmlDoc.DocumentNode.SelectSingleNode("//link[@rel='image_src']");
                    if (linkImageSrcTag != null)
                    {
                        return linkImageSrcTag.GetAttributeValue("href", null);
                    }

                    var thumbnailMetaTag = htmlDoc.DocumentNode.SelectSingleNode("//meta[@name='thumbnail']");
                    if (thumbnailMetaTag != null)
                    {
                        return thumbnailMetaTag.GetAttributeValue("content", null);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }

            return "https://picsum.photos/1000/666";
        }

        private static async Task<(string token, string did)> BSkyGetAccessToken(string username, string password)
        {
            using var client = new HttpClient();
            var loginUrl = $"https://bsky.social/xrpc/com.atproto.server.createSession";

            var payload = new Dictionary<string, string>
            {
                { "identifier", username },
                { "password", password }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(loginUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {response.StatusCode}");
                Environment.Exit(-1);
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonDocument>(jsonResponse);

            var token = data?.RootElement.GetProperty("accessJwt").GetString() ?? "";

            var did = data?.RootElement.GetProperty("did").GetString() ?? "";

            return (token, did);

        }

        private static string GetImageMimeType(string imageUrl)
        {
            var extension = Path.GetExtension(imageUrl).ToLowerInvariant();

            return extension switch
            {
                ".png" => "image/png",
                ".jpeg" => "image/jpeg",
                ".jpg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };
        }

        private static async Task<string> BSkyUploadImage(string token, string imageUrl)
        {
            string imageMimeType = GetImageMimeType(imageUrl);

            // Download the image
            byte[] imageData;
            using (var httpClient = new HttpClient())
            {
                imageData = await httpClient.GetByteArrayAsync(imageUrl);
            }

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            // Upload the image
            var uploadUrl = $"https://bsky.social/xrpc/com.atproto.repo.uploadBlob";
            using var imageContent = new ByteArrayContent(imageData);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(imageMimeType);

            var response = await client.PostAsync(uploadUrl, imageContent);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error uploading image: {response.StatusCode}");
                Environment.Exit(-1);
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var responseObj = JsonSerializer.Deserialize<Dictionary<string, object>>(responseJson);
            return responseObj?["blob"].ToString() ?? ""; // Retrieve the blob reference

        }

        private static List<Dictionary<string, object>> BSkyCreateFacets(string tags, int offset)
        {
            var facets = new List<Dictionary<string, object>>();
            var hashtags = tags.Split(' ');

            int currentIndex = 0;
            foreach (var tag in hashtags)
            {
                if (tag.StartsWith("#"))
                {
                    int startIndex = currentIndex;
                    int endIndex = startIndex + tag.Length;

                    var facet = new Dictionary<string, object>
                    {
                        { "index", new Dictionary<string, int>
                            {
                                { "byteStart", startIndex + offset + 2 },
                                { "byteEnd", endIndex + offset + 2 }
                            }
                        },
                        { "features", new List<Dictionary<string, string>>
                            {
                                new Dictionary<string, string>
                                {
                                    { "$type", "app.bsky.richtext.facet#tag" },
                                    { "tag", tag.Substring(1) }
                                }
                            }
                        }
                    };

                    facets.Add(facet);
                    currentIndex = endIndex + 1; // Update currentIndex to the next position
                }
            }

            return facets;
        }

        private static async Task<bool> BSkyCreateRecord(string token, string did, string content, string url, string tags, string blobRef)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

            var postUrl = $"https://bsky.social/xrpc/com.atproto.repo.createRecord";

            var blobRef2 = JsonSerializer.Deserialize<object>(blobRef) ?? "";

            var facets = BSkyCreateFacets(tags, content.Length);

            var payload = new Dictionary<string, object>
                {
                    { "repo", did },
                    { "collection", "app.bsky.feed.post" },
                    { "record", new Dictionary<string, object>
                        {
                            { "$type", "app.bsky.feed.post" },
                            { "text", content + Environment.NewLine + tags },
                            { "facets", facets
                            },
                            { "createdAt", DateTime.UtcNow.ToString("o") },
                            { "embed", new Dictionary<string, object>
                                {
                                    { "$type", "app.bsky.embed.external" },
                                    { "external", new Dictionary<string, object>
                                        {
                                            { "uri", url },
                                            { "title", content },
                                            { "description", url },
                                            { "thumb", blobRef2 },
                                        }
                                    }
                                }
                            }

                        }
                    }
                };

            //var payload2 = new Dictionary<string, object>
            //    {
            //        { "repo", did },
            //        { "collection", "app.bsky.feed.post" },
            //        { "record", new Dictionary<string, object>
            //            {
            //                { "$type", "app.bsky.feed.post" },
            //                { "text", content },
            //                { "createdAt", DateTime.UtcNow.ToString("o") },
            //                { "embed", new Dictionary<string, object>
            //                    {
            //                        { "$type", "app.bsky.embed.images" },
            //                        { "images", new List<Dictionary<string, object>>
            //                            {
            //                                new Dictionary<string, object>
            //                                {
            //                                    { "image", blobRef2 }, // Use the blob reference here
            //                                    { "alt", "An image uploaded from a URL" }
            //                                }
            //                            }
            //                        }
            //                    }
            //                }

            //            }
            //        }
            //    };



            var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

            var contentData = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(postUrl, contentData);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Error: {response.StatusCode} - {errorContent}");
            }
            return response.IsSuccessStatusCode;
        }

        private static async Task<bool> BSkyPost( string postContent, string postLink, string postTags)
        {

            // Step 1: Authenticate 
            var (token, did) = await BSkyGetAccessToken(username, password);
            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine("Failed to authenticate.");
                return false;
            }

            // Step 2: Upload the image and get the reference
            string postImageUrl = await GetTwitterImageFromUrl(postLink);
            var blobRef = await BSkyUploadImage(token, postImageUrl);
            if (string.IsNullOrEmpty(blobRef))
            {
                Console.WriteLine("Failed to upload the image.");
                return false;
            }

            // Step 3: Post a status with the image
            bool success = await BSkyCreateRecord(token, did, postContent, postLink, postTags, blobRef);

            return success;

        }

        static async Task Main(string[] args)
        {
            var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";

            var builder = new ConfigurationBuilder()
                   .SetBasePath(Directory.GetCurrentDirectory())
                   .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                   .AddJsonFile($"appsettings.development.json", optional: false)
                   .AddEnvironmentVariables();

            IConfigurationRoot configuration = builder.Build();

            username = configuration["BSUsername"] ?? "";
            password = configuration["BSPassword"] ?? "";

            string postTags = "#Microsoft #Azure #AppDev";
            //string postContent = "How to generate unit tests with GitHub Copilot: Tips and examples";
            //string postLink = "https://github.blog/ai-and-ml/how-to-generate-unit-tests-with-github-copilot-tips-and-examples/";
            string postContent = "The top 10 gifts for the developer in your life";
            string postLink = "https://github.blog/news-insights/company-news/the-top-10-gifts-for-the-developer-in-your-life/";

            bool success = await BSkyPost(postContent, postLink, postTags);

            Console.WriteLine(success ? "Post with image successful!" : "Failed to post.");

        }
    }

}
