using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace libAudioCopy_Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TestingController : ControllerBase
    {
        private bool CheckToken(string? token)
        {
            return (Environment.GetEnvironmentVariable("AudioClone_Token") ?? throw new ArgumentNullException("Please define token.")) == token;
        }
        [HttpGet("LatencyTest")]
        public ActionResult<string> LatencyTest(string token)
        {
            if (!CheckToken(token))
            {
                return Unauthorized("Unauthorized, please check your token."); ;
            }
            return DateTime.Now.Ticks.ToString();
        }

        [HttpGet("SpeedTest")]
        public IActionResult SpeedTest(string token)
        {
            if (!CheckToken(token))
            {
                return Unauthorized("Unauthorized, please check your token."); ;
            }
            var data = Enumerable.Repeat<byte>(42, 15 * 1024 * 1024).ToArray();
            return File(data, "text/plain", "speedtest.txt");
        }

        [HttpGet("/index")]
        public async Task Index(string? token = "")
        {
            Response.ContentType = "text/html";
            if (token is null || !CheckToken(token))
            {
                Response.StatusCode = StatusCodes.Status401Unauthorized;
                string html1 =
    $"""
<!DOCTYPE html><html><head><meta charset='utf-8'/>
    <title>AudioClone</title>
</head>
<body>  
    <a href="https://github.com/0xeeeeeeeeeeee/AudioClone">AudioClone</a>
    <br />
    This server is part of AudioClone or AudioCopy.
</body>
</html>
""";
                await Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes(html1));
                return;
            }
            string html = @$"
<!DOCTYPE html><html><head><meta charset='utf-8'/><title>AudioClone</title></head><body>
  <h3>WAV</h3><audio controls src='/api/audio/wav?token={token}'></audio>
  <h3>FLAC</h3><audio controls src='/api/audio/flac?token={token}'></audio>
</body></html>";
            await Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes(html));
        }

    }
}
