using System;
using System.IO;
using OLock;

namespace OLock.Tests
{
    // 版本解析比较、GitHub Release JSON 选包解析与 PE 头校验的单元测试
    public class UpdateServiceTests
    {
        [Theory]
        [InlineData("1.4.1", 1, 4, 1, 0)]
        [InlineData("v1.4.1", 1, 4, 1, 0)]
        [InlineData("V1.5.0", 1, 5, 0, 0)]
        [InlineData("1.5", 1, 5, 0, 0)]
        [InlineData("1.4.1+0ec04c8", 1, 4, 1, 0)]
        [InlineData("v2.0.0-beta.1", 2, 0, 0, 0)]
        public void TryParseVersion_NormalizesValidVersions(string raw, int major, int minor, int build, int rev)
        {
            Assert.True(UpdateService.TryParseVersion(raw, out Version v));
            Assert.Equal(new Version(major, minor, build, rev), v);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-version")]
        [InlineData("v")]
        public void TryParseVersion_RejectsInvalidStrings(string raw)
        {
            Assert.False(UpdateService.TryParseVersion(raw, out _));
        }

        [Theory]
        [InlineData("1.4.1", "v1.4.2", true)]
        [InlineData("1.4.1+abc", "v1.5.0", true)]
        [InlineData("1.4.0", "v1.4", false)]   // 归一化后相等，不算新版本
        [InlineData("1.4.1", "v1.4.1", false)]
        [InlineData("1.5.0", "v1.4.9", false)]
        public void IsNewerVersion_ComparesNormalizedVersions(string current, string latest, bool expected)
        {
            Assert.Equal(expected, UpdateService.IsNewerVersion(current, latest));
        }

        [Fact]
        public void ParseLatestReleaseJson_SelectsMatchingLiteAssetWhenNewer()
        {
            string json = """
            {
              "tag_name": "v1.5.0",
              "html_url": "https://github.com/sLingli/O-Lock/releases/tag/v1.5.0",
              "body": "- feat: check for updates",
              "assets": [
                {
                  "name": "O-Lock-Full.zip",
                  "size": 63000000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.5.0/O-Lock-Full.zip"
                },
                {
                  "name": "O-Lock-Lite.zip",
                  "size": 170000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.5.0/O-Lock-Lite.zip"
                }
              ]
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.4.1", "Lite");

            Assert.Equal("UpdateAvailable", res.Status.ToString());
            Assert.Equal("1.5.0", res.LatestVersion);
            Assert.Equal("O-Lock-Lite.zip", res.AssetName);
            Assert.Equal(170000, res.AssetSize);
            Assert.Contains("O-Lock-Lite.zip", res.DownloadUrl);
            Assert.Contains("check for updates", res.ReleaseNotes);
        }

        [Fact]
        public void ParseLatestReleaseJson_SelectsMatchingFullAsset()
        {
            string json = """
            {
              "tag_name": "v1.5.0",
              "html_url": "https://github.com/sLingli/O-Lock/releases/tag/v1.5.0",
              "body": "notes",
              "assets": [
                {
                  "name": "O-Lock-Lite.zip",
                  "size": 170000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.5.0/O-Lock-Lite.zip"
                },
                {
                  "name": "O-Lock-Full.zip",
                  "size": 63000000,
                  "browser_download_url": "https://github.com/sLingli/O-Lock/releases/download/v1.5.0/O-Lock-Full.zip"
                }
              ]
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.4.1", "Full");

            Assert.Equal("UpdateAvailable", res.Status.ToString());
            Assert.Equal("O-Lock-Full.zip", res.AssetName);
            Assert.Equal(63000000, res.AssetSize);
        }

        [Fact]
        public void ParseLatestReleaseJson_SameOrOlderVersion_ReportsUpToDate()
        {
            string json = """
            {
              "tag_name": "v1.4.1",
              "html_url": "https://github.com/sLingli/O-Lock/releases/tag/v1.4.1",
              "body": "",
              "assets": []
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.4.1", "Lite");

            Assert.Equal("UpToDate", res.Status.ToString());
            Assert.Equal("1.4.1", res.LatestVersion);
        }

        [Fact]
        public void ParseLatestReleaseJson_RejectsNonHttpsAssetUrl()
        {
            string json = """
            {
              "tag_name": "v1.5.0",
              "html_url": "http://insecure.example.com/release",
              "body": "",
              "assets": [
                {
                  "name": "O-Lock-Lite.zip",
                  "size": 170000,
                  "browser_download_url": "http://insecure.example.com/O-Lock-Lite.zip"
                }
              ]
            }
            """;

            var res = UpdateService.ParseLatestReleaseJson(json, "1.4.1", "Lite");

            Assert.Equal("UpdateAvailable", res.Status.ToString());
            Assert.Null(res.DownloadUrl);
            Assert.Equal(UpdateService.ReleasesPageUrl, res.HtmlUrl);
        }

        [Fact]
        public void ParseLatestReleaseJson_InvalidJson_ReportsFailed()
        {
            var res = UpdateService.ParseLatestReleaseJson("{broken", "1.4.1", "Lite");
            Assert.Equal("Failed", res.Status.ToString());
            Assert.False(string.IsNullOrEmpty(res.ErrorMessage));
        }

        [Fact]
        public void IsValidPeExecutable_ChecksSizeAndMzHeader()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"olock_pe_test_{Guid.NewGuid():N}.exe");
            try
            {
                // 过小文件应拒绝
                File.WriteAllBytes(tempFile, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
                Assert.False(UpdateService.IsValidPeExecutable(tempFile));

                // 足够大且带 MZ 头应通过
                byte[] validBuf = new byte[60 * 1024];
                validBuf[0] = 0x4D; // 'M'
                validBuf[1] = 0x5A; // 'Z'
                File.WriteAllBytes(tempFile, validBuf);
                Assert.True(UpdateService.IsValidPeExecutable(tempFile));

                // 足够大但非 MZ 头应拒绝
                validBuf[0] = 0x50; // 'P' (例如 ZIP 头 PK)
                validBuf[1] = 0x4B;
                File.WriteAllBytes(tempFile, validBuf);
                Assert.False(UpdateService.IsValidPeExecutable(tempFile));
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
    }
}
