using System.Text.Json;
using AvPlatform.WebApi.Channels;

namespace AvPlatform.WebApi.Tests;

[TestClass]
public sealed class YxfmProtocolTests
{
    [TestMethod]
    public void EncryptAndDecrypt_WithPayload_PreservesJson()
    {
        var encrypted = YxfmApiClient.Encrypt(new
        {
            v = "radio_album_info",
            album_id = "1149",
            device_type = "2"
        });

        using var decrypted = YxfmApiClient.Decrypt(encrypted);

        Assert.AreEqual("radio_album_info", decrypted.RootElement.GetProperty("v").GetString());
        Assert.AreEqual("1149", decrypted.RootElement.GetProperty("album_id").GetString());
        Assert.AreEqual("2", decrypted.RootElement.GetProperty("device_type").GetString());
    }

    [TestMethod]
    public void MapHome_WithDuplicateAlbums_DeduplicatesAndIgnoresAds()
    {
        using var document = JsonDocument.Parse("""
        {
          "ad_list": [{ "title": "广告" }],
          "longbookAlbumList": [{
            "radio_album_id": "1149",
            "name": "一个女作家的经历",
            "cover_img": "https://img/1149.jpg",
            "desc": "简介",
            "host_name": "姽狐",
            "radio_count": "6",
            "hot_number": "7475.3",
            "categorys": { "child_category": { "name": "情色长篇" } }
          }],
          "likeAlbumList": [{
            "radio_album_id": "1149",
            "name": "重复专辑"
          }]
        }
        """);

        var items = YxfmResponseMapper.MapHome(document.RootElement);

        var item = Assert.ContainsSingle(items);
        Assert.AreEqual("1149", item.Id);
        Assert.AreEqual("一个女作家的经历", item.Title);
        Assert.AreEqual(6, item.EpisodeCount);
        Assert.AreEqual(7475.3m, item.Popularity);
    }

    [TestMethod]
    public void MapDetail_WithPaidEpisodes_OnlyMarksFreeEpisodePlayable()
    {
        using var document = CreateDetailDocument();

        var detail = YxfmResponseMapper.MapDetail(document.RootElement, "yxfm", "有声（YXFM）");

        Assert.AreEqual("1149", detail.Id);
        Assert.HasCount(2, detail.Episodes);
        Assert.IsTrue(detail.Episodes[0].IsPlayable);
        Assert.IsFalse(detail.Episodes[1].IsPlayable);
        Assert.IsTrue(detail.IsPaid);
    }

    [TestMethod]
    public void MapPlaySource_WithFreeEpisode_PrefersHls()
    {
        using var document = CreateDetailDocument();

        var source = YxfmResponseMapper.MapPlaySource(document.RootElement, "16442");

        Assert.IsNotNull(source);
        Assert.IsTrue(source.IsPlayable);
        Assert.AreEqual("https://media/audio.m3u8", source.SourceUrl);
        Assert.AreEqual("application/vnd.apple.mpegurl", source.MediaType);
    }

    [TestMethod]
    public void MapPlaySource_WithPaidEpisode_DoesNotExposeUrl()
    {
        using var document = CreateDetailDocument();

        var source = YxfmResponseMapper.MapPlaySource(document.RootElement, "16441");

        Assert.IsNotNull(source);
        Assert.IsFalse(source.IsPlayable);
        Assert.IsNull(source.SourceUrl);
    }

    [TestMethod]
    public void RepairText_WithMojibake_RestoresUtf8Text()
    {
        var repaired = YxfmResponseMapper.RepairText("涓コ浣滃");

        Assert.AreEqual("个女作家", repaired);
    }

    private static JsonDocument CreateDetailDocument() => JsonDocument.Parse("""
    {
      "radio_album_id": "1149",
      "name": "一个女作家的经历",
      "cover_img": "https://img/1149.jpg",
      "desc": "简介",
      "radio_count": "2",
      "hot_number": "7477.5",
      "is_finished": "1",
      "price": 0,
      "host": { "name": "姽狐" },
      "categorys": { "child_category": { "name": "情色长篇" } },
      "radio_list": [
        {
          "radio_id": "16442",
          "name": "第一集",
          "duration": "00:25:38",
          "is_free": "1",
          "url": "https://media/audio.m3u8",
          "down_url": "https://media/audio.mp3"
        },
        {
          "radio_id": "16441",
          "name": "第二集",
          "duration": "00:25:05",
          "is_free": "0",
          "url": "https://media/paid.m3u8"
        }
      ]
    }
    """);
}
