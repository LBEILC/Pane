using Pane.Contracts;
using Pane.Services.Ai;
using Pane.Services.Cloud;
using Pane.Settings;
using System.Diagnostics;
using System.Text.Json;
using Windows.Graphics.Imaging;

internal static partial class Program
{
    const string RedPng = "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAEElEQVR4nGP8zwAFTDAGAwATKQED8NgHhAAAAABJRU5ErkJggg==";

    static async Task ImageRequestTests(AiService ai, SettingsStore store, FakeServer server)
    {
        bool account = store.Current.Ai.Provider == AiProvider.ChatGpt;
        bool anthropic = !account && store.Current.Ai.CurrentEndpoint.Format == AiApiFormat.Anthropic;
        string path = Path.Combine(store.DirectoryPath, "red.jpg");
        File.WriteAllBytes(path, Convert.FromBase64String(RedPng));
        async Task Send(params AiChatMessage[] messages)
        {
            await foreach (var _ in ai.ChatAsync(messages, "probe", default)) { }
        }
        await Send(new AiChatMessage(AiChatRole.User, "look", []));
        // 固定旧协议的属性顺序、标点、转义和文字消息类型。
        const string prefix = "{\"model\":\"probe\",\"stream\":true,";
        string system = JsonEncodedText.Encode("你在 Windows 小工具 Pane 里回答。回答简洁直接；除非用户要求，否则用提问所用的语言；尽量用纯文本，少用 Markdown。").ToString();
        string expected = prefix + (account ? "\"store\":false,\"instructions\":\"" + system + "\",\"input\":[" :
            anthropic ? "\"max_tokens\":16384,\"system\":\"" + system + "\",\"messages\":[" :
            "\"messages\":[{\"role\":\"system\",\"content\":\"" + system + "\"},") + "{\"role\":\"user\",\"content\":\"look\"}]}";
        Check(server.LastBodyBytes.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(expected)), "纯文字请求与旧版字节一致");
        AiChatMessage first = new(AiChatRole.User, "look", [path,path]);
        foreach (bool followup in new[]{false,true})
        {
            await Send(followup ? [first,new(AiChatRole.Assistant,"answer"),new(AiChatRole.User,"again")] : [first]);
            using var doc = JsonDocument.Parse(server.LastBody!);
            var turns = doc.RootElement.GetProperty(account ? "input" : "messages");
            int offset = account || anthropic ? 0 : 1;
            var content = turns[offset].GetProperty("content");
            Check(content.GetArrayLength()==3 && content[anthropic ? 2 : 0].GetProperty("text").GetString()=="look" &&
                content[anthropic ? 2 : 0].GetProperty("type").GetString()==(account?"input_text":"text"), "首条保留文字");
            for (int i=0;i<2;i++)
            {
                var image = content[anthropic ? i : i+1];
                if (anthropic)
                {
                    var source = image.GetProperty("source");
                    Check(image.GetProperty("type").GetString()=="image" && source.GetProperty("type").GetString()=="base64" &&
                        source.GetProperty("media_type").GetString()=="image/png" && source.GetProperty("data").GetString()==RedPng, "Messages 图片");
                }
                else
                {
                    var url = image.GetProperty("image_url");
                    Check(image.GetProperty("type").GetString()==(account?"input_image":"image_url") &&
                        (account?url:url.GetProperty("url")).GetString()=="data:image/png;base64,"+RedPng, "OpenAI 图片");
                }
            }
            if (followup) Check(turns[offset+1].GetProperty("content").GetString()=="answer" &&
                turns[offset+2].GetProperty("content").GetString()=="again", "后续轮次只带文字");
        }
        var original = server.Fixture;
        try
        {
            foreach (string message in new[]{"Invalid content type. image_url is only supported by certain models.",
                "This text-only model does not support image input.", "模型不支持多模态"})
            {
                server.Fixture = (_,_) => (400,"{\"error\":{\"message\":\""+message+"\"}}","application/json");
                try { await Send(first); throw new Exception("缺少模型能力错误"); }
                catch (AiUnsupportedInputException e) { Check(e.Message=="当前模型不支持识别图片，换一个支持识图的模型再试","能力错误文案"); }
            }
            server.Fixture = (_,_) => (400,"{\"error\":{\"message\":\"unsupported image format\"}}","application/json");
            await Error<AiCredentialException>(()=>Send(first),"格式错误保留原分类");
        }
        finally { server.Fixture = original; }
    }

    static async Task MeasureImage(string path)
    {
        var watch = Stopwatch.StartNew();
        var image = await AiImage.Read(path, default);
        watch.Stop();
        using var stream = new MemoryStream(image.Bytes).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Console.WriteLine($"{Path.GetFileName(path)} {new FileInfo(path).Length} -> {decoder.PixelWidth}x{decoder.PixelHeight} {image.MediaType} {image.Bytes.Length} bytes {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    static async Task<bool> CanDecodeWebp(byte[] sample)
    {
        using var stream = new MemoryStream(sample).AsRandomAccessStream();
        try { await BitmapDecoder.CreateAsync(BitmapDecoder.WebpDecoderId, stream); return true; }
        catch (Exception e) when (e.HResult is unchecked((int)0x88982F8B) or unchecked((int)0x80040154))
        { return false; }
    }

    static async Task ImageProcessingTests(string directory)
    {
        string path = Path.Combine(directory, "fixture.bin");
        async Task<AiImage> Read(byte[] bytes)
        {
            var fixture = Path.Combine(directory, Guid.NewGuid()+".bin");
            File.WriteAllBytes(fixture, bytes);
            return await AiImage.Read(fixture, default);
        }
        var png = Convert.FromBase64String(RedPng);
        Check((await Read(png)).Bytes.SequenceEqual(png),"小 PNG 原样");
        foreach (string data in new[]{
            "R0lGODdhAwACAIEAAP8AAAAAAAAAAAAAACwAAAAAAwACAAAIBgABCBwYEAAh+QQBAAABACwAAAAAAwACAIEAAP8AAAAAAAAAAAAIBgABCBwYEAA7",
            "UklGRjwAAABXRUJQVlA4IDAAAADQAQCdASoDAAIAAUAmJaACdLoB+AADsAD+8ut//NgVzXPv9//S4P0uD9Lg/9KQAAA=",
            "Qk1OAAAAAAAAADYAAAAoAAAAAwAAAAIAAAABABgAAAAAABgAAADEDgAAxA4AAAAAAAAAAAAAAAD/AAD/AAD/AAAAAAD/AAD/AAD/AAAA"})
        {
            var bytes = Convert.FromBase64String(data);
            if (bytes.AsSpan(0,4).SequenceEqual("RIFF"u8) && !await CanDecodeWebp(bytes))
            {
                try { await Read(bytes); throw new Exception("缺少 WebP 解码器时应提示原因"); }
                catch (AiUnsupportedInputException e) { Check(e.Message.Contains("WebP 解码器"), "缺少系统 WebP 解码器"); }
                Console.WriteLine("WebP：系统解码器不可用，已验证读取失败的原因");
                continue;
            }
            var jpeg = await Read(bytes);
            Check(jpeg.MediaType=="image/jpeg" && (await Read(jpeg.Bytes)).Bytes.SequenceEqual(jpeg.Bytes),"不透明转 JPEG，小 JPEG 原样");
            using var stream = new MemoryStream(jpeg.Bytes).AsRandomAccessStream();
            var frame = await BitmapDecoder.CreateAsync(stream);
            var provider = await frame.GetPixelDataAsync(BitmapPixelFormat.Rgba8,BitmapAlphaMode.Ignore,new(),
                ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage);
            var pixel = provider.DetachPixelData();
            if (bytes.AsSpan(0,4).SequenceEqual("RIFF"u8)) Console.WriteLine("WebP：系统解码器已转码有效样本");
            Check(pixel[0]>240 && pixel[2]<15,"按内容解码红色第一帧");
        }
        const int width=2400,height=1800;
        byte[] noise=new byte[width*height*4];
        new Random(162).NextBytes(noise);
        foreach(bool alpha in new[]{false,true})
        {
            path = Path.Combine(directory, $"noise-{alpha}.png");
            using (var output=new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,output);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8,alpha?BitmapAlphaMode.Straight:BitmapAlphaMode.Ignore,width,height,96,96,noise);
                await encoder.FlushAsync();
                output.Seek(0);
                using var input=output.AsStreamForRead();
                using var file=File.Create(path);
                await input.CopyToAsync(file);
            }
            Check(new FileInfo(path).Length>AiImage.MaxBytes,"输入超过 4 MiB");
            var image=await AiImage.Read(path,default);
            using var stream=new MemoryStream(image.Bytes).AsRandomAccessStream();
            var frame=await BitmapDecoder.CreateAsync(stream);
            Check(image.Bytes.Length<=AiImage.MaxBytes && frame.PixelWidth*3==frame.PixelHeight*4,"输出大小和比例");
            Check(alpha ? image.MediaType=="image/png" && frame.PixelWidth<2048 :
                image.MediaType=="image/jpeg" && frame.PixelWidth==2048,"透明图缩尺寸，不透明图缩至 2048");
        }
        await Error<AiUnsupportedInputException>(()=>Read([1,2,3]),"坏文件");
        await Error<AiUnsupportedInputException>(()=>AiImage.Read(path+".missing",default),"不存在的文件");
        path=Path.Combine(directory,"duplicate.png");
        File.WriteAllBytes(path,png);
        var prepared=await AiImage.Prepare([new(AiChatRole.User,"", [path,path])],default);
        Check(prepared?.Count==1,"同一请求只处理一次");
        await Error<OperationCanceledException>(()=>AiImage.Prepare([new(AiChatRole.User,"",[path])],new(true)),"可取消");
        var actions=Enumerable.Range(0,14).Select(i=>new AiImageAction($"img-{i:x8}",new string('名',13),new string('文',2001))).ToArray();
        AiImageAction[] dirty=[new("img-DEADBEEF","a","b"),new("ocr","a","b"),new("img-12345678"," ","x"),
            new("img-12345679","x"," "),actions[0],..actions];
        var store=new SettingsStore(directory,Defaults());
        var settings=Defaults() with{Ai=new(){ImageActions=dirty,LastImageAction="describe"}};
        File.WriteAllText(Path.Combine(directory,"settings.json"),JsonSerializer.Serialize(settings,SettingsJson.Default.PaneSettings));
        store.Load();
        var clean=store.Current.Ai.ImageActions!;
        Check(clean.Select(a=>a.Id).SequenceEqual(actions.Take(12).Select(a=>a.Id)),"过滤、去重、顺序及上限");
        Check(clean.All(a=>a.Name.Length==12 && a.Prompt.Length==2000),"名称和提示词截断");
        var exported=JsonSerializer.SerializeToNode(store.Current,SettingsJson.Default.PaneSettings)!.AsObject();
        LocalSettingsFields.Apply(exported,null);
        LocalSettingsFields.Apply(exported,new(){["Ai"]=new System.Text.Json.Nodes.JsonObject{["LastImageAction"]="ocr"}});
        var imported=exported.Deserialize(SettingsJson.Default.PaneSettings)!.Ai;
        Check(imported.LastImageAction=="describe" && imported.ImageActions!.SequenceEqual(clean),"设置导出和导入带上两个字段");
        var leaves=SyncData.Leaves(store.Current);
        Check(leaves["Ai.ImageActions"]!.AsArray().Count==12 && !leaves.ContainsKey("Ai.LastImageAction"),"同步范围");
        Check(SyncData.Apply(store.Current,new(){["Ai.LastImageAction"]="ocr"}).Ai.LastImageAction=="describe","同步保留本机动作");
    }
}
