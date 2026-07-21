using UnityEngine;
using Unity.WebRTC;
using NativeWebSocket;
using System;using System.Collections;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;

public class WebRTC_RenderTexture_Streamer : MonoBehaviour{[Header("WebSocket Signaling Server URL")]public string signalingServerUrl = "wss://webrtc-signaling-server-66y1.onrender.com";

[Header("Reference camera (visual source)")]
public Camera MocopiCamera;

[Header("RenderTexture size")]
public int width = 1280;
public int height = 720;

[Header("Use dedicated capture camera")]
public bool useDedicatedCaptureCamera = true;

[Header("Intervals")]
public int statsIntervalMs = 1000;

private Camera captureCam;
private RenderTexture rt;

private WebSocket ws;
private RTCPeerConnection pc;

private VideoStreamTrack videoTrack;
private RTCRtpSender videoSender;
private RTCRtpTransceiver videoTransceiver;

private bool remoteDescSet = false;
private readonly List<RTCIceCandidateInit> pendingRemoteCandidates = new List<RTCIceCandidateInit>();

private Coroutine wsPumpCoroutine;
private Coroutine statsCoroutine;
private Coroutine rtProbeCoroutine;

private IEnumerator Start()
{
    Debug.Log(":white_check_mark: WebRTC_RenderTexture_Streamer Start()");

    if (MocopiCamera == null)
    {
        Debug.LogError(":x: MocopiCamera is not assigned.");
        yield break;
    }

    // ★IMPORTANT (pre.*): WebRTC.Update() should be run as a coroutine
    StartCoroutine(WebRTC.Update());

    // ---- RenderTexture (B8G8R8A8_SRGB) ----
    var desc = new RenderTextureDescriptor(width, height)
    {
        depthBufferBits = 24,
        msaaSamples = 1,
        graphicsFormat = GraphicsFormat.B8G8R8A8_SRGB,
        sRGB = true
    };

    rt = new RenderTexture(desc);
    rt.name = "WebRTC_CaptureRT";
    rt.Create();

    Debug.Log($":white_check_mark: RT created: {rt.name} size={rt.width}x{rt.height} graphicsFormat={rt.graphicsFormat} sRGB={rt.sRGB}");

    // ---- Capture camera ----
    if (useDedicatedCaptureCamera)
    {
        var go = new GameObject("WebRTC_CaptureCamera");
        captureCam = go.AddComponent<Camera>();

        captureCam.transform.position = MocopiCamera.transform.position;
        captureCam.transform.rotation = MocopiCamera.transform.rotation;
        captureCam.fieldOfView = MocopiCamera.fieldOfView;
        captureCam.nearClipPlane = MocopiCamera.nearClipPlane;
        captureCam.farClipPlane = MocopiCamera.farClipPlane;
        captureCam.cullingMask = MocopiCamera.cullingMask;
        captureCam.clearFlags = MocopiCamera.clearFlags;
        captureCam.backgroundColor = MocopiCamera.backgroundColor;

        captureCam.targetTexture = rt;
        try { captureCam.forceIntoRenderTexture = true; } catch { }

        Debug.Log(":white_check_mark: Dedicated capture camera created and set targetTexture.");
    }
    else
    {
        MocopiCamera.targetTexture = rt;
        try { MocopiCamera.forceIntoRenderTexture = true; } catch { }
        captureCam = MocopiCamera;
        Debug.Log(":white_check_mark: Using MocopiCamera directly for RT capture.");
    }

    yield return new WaitForEndOfFrame();

    // Probe RT pixels (should not be black if rendering)
    rtProbeCoroutine = StartCoroutine(RenderTextureProbeLoop());

    // ---- WebSocket ----
    ws = new WebSocket(signalingServerUrl);

    ws.OnOpen += () =>
    {
        Debug.Log(":white_check_mark: Connected to signaling server");
        SendWS(new Sig { role = "unity" });
    };

    ws.OnMessage += (bytes) =>
    {
        string msg = System.Text.Encoding.UTF8.GetString(bytes);
        Debug.Log(":envelope_with_arrow: WS recv: " + msg);
        HandleWS(msg);
    };

    ws.OnError += (e) => Debug.LogError(":x: WS error: " + e);

    var connectTask = ws.Connect();
    while (ws.State != WebSocketState.Open && !connectTask.IsCompleted)
        yield return null;

    if (ws.State != WebSocketState.Open)
    {
        Debug.LogError(":x: WS failed to open. State=" + ws.State);
        yield break;
    }

    // Pump WS queue each frame (separate coroutine)
    wsPumpCoroutine = StartCoroutine(WsPumpLoop());

    // ---- PeerConnection ----
    var config = new RTCConfiguration
    {
        iceServers = new[]
        {
            new RTCIceServer { urls = new[] { "stun:stun.relay.metered.ca:80" } },
            new RTCIceServer
            {
                urls = new[]
                {
                    "turn:standard.relay.metered.ca:80",
                    "turn:standard.relay.metered.ca:80?transport=tcp",
                    "turn:standard.relay.metered.ca:443",
                    "turns:standard.relay.metered.ca:443?transport=tcp"
                },
                username = "YOUR_TURN_USERNAME",
                credential = "YOUR_TURN_CREDENTIAL"
            }
        }
    };

    pc = new RTCPeerConnection(ref config);

    pc.OnIceConnectionChange = st => Debug.Log(":ice_cube: ICE: " + st);
    pc.OnConnectionStateChange = st => Debug.Log(":link: Conn: " + st);

    pc.OnIceCandidate = c =>
    {
        if (c == null) return;
        SendWS(new Sig
        {
            type = "candidate",
            candidate = c.Candidate,
            sdpMid = c.SdpMid,
            sdpMLineIndex = c.SdpMLineIndex
        });
    };

    // ---- Track creation (pre.8 compatible: no CopyTexture flag) ----
    Debug.Log(":movie_camera: Create VideoStreamTrack from RenderTexture...");
    videoTrack = new VideoStreamTrack(rt);
    Debug.Log(":white_check_mark: VideoStreamTrack created OK");

    // ---- Prefer transceiver SendOnly (if available), else AddTrack ----
    bool transceiverOK = false;
    try
    {
        videoTransceiver = pc.AddTransceiver(TrackKind.Video);
        videoTransceiver.Direction = RTCRtpTransceiverDirection.SendOnly;
        videoSender = videoTransceiver.Sender;
        videoSender.ReplaceTrack(videoTrack);
        transceiverOK = true;
        Debug.Log(":white_check_mark: AddTransceiver(Video) + SendOnly + ReplaceTrack OK");
    }
    catch (Exception e)
    {
        Debug.LogWarning(":warning: Transceiver path failed: " + e.Message);
    }

    if (!transceiverOK)
    {
        videoSender = pc.AddTrack(videoTrack);
        Debug.Log(":white_check_mark: AddTrack fallback. sender=" + (videoSender != null));
    }

    // ---- Offer ----
    var offerOp = pc.CreateOffer();
    yield return offerOp;

    var offer = offerOp.Desc;

    Debug.Log("===== OFFER SDP (direction lines) BEGIN =====\n" + ExtractDirectionLines(offer.sdp) + "===== OFFER SDP END =====");

    var setLocalOp = pc.SetLocalDescription(ref offer);
    yield return setLocalOp;

    SendWS(new Sig { type = "offer", sdp = offer.sdp });
    Debug.Log(":outbox_tray: Offer sent");

    statsCoroutine = StartCoroutine(StatsLoop());
}

private IEnumerator WsPumpLoop()
{
    while (true)
    {
        ws?.DispatchMessageQueue();
        yield return null;
    }
}

private string ExtractDirectionLines(string sdp)
{
    var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    var sb = new System.Text.StringBuilder();

    foreach (var l in lines)
    {
        if (l.StartsWith("m=video") ||
            l.StartsWith("a=send") ||
            l.StartsWith("a=recv") ||
            l.StartsWith("a=inactive") ||
            l.StartsWith("a=mid"))
        {
            sb.AppendLine(l);
        }
    }
    return sb.ToString();
}

private IEnumerator StatsLoop()
{
    var wait = new WaitForSeconds(statsIntervalMs / 1000f);

    while (true)
    {
        var op = pc.GetStats();
        yield return op;

        bool anyOutbound = false;

        foreach (var stat in op.Value.Stats.Values)
        {
            if (stat.Type != RTCStatsType.OutboundRtp) continue;

            anyOutbound = true;

            string id = stat.Id;
            string kind = stat.Dict.TryGetValue("kind", out var k) ? (k?.ToString() ?? "null") : "(no kind)";
            ulong bytes = stat.Dict.TryGetValue("bytesSent", out var b) ? Convert.ToUInt64(b) : 0;
            uint frames = stat.Dict.TryGetValue("framesEncoded", out var f) ? Convert.ToUInt32(f) : 0;
            uint packets = stat.Dict.TryGetValue("packetsSent", out var p) ? Convert.ToUInt32(p) : 0;

            Debug.Log($"[OUT][{id}] kind={kind} bytesSent={bytes} packetsSent={packets} framesEncoded={frames}");
        }

        if (!anyOutbound) Debug.Log("[OUT] outbound-rtp not found");

        yield return wait;
    }
}

private IEnumerator RenderTextureProbeLoop()
{
    var wait = new WaitForSeconds(1f);
    var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);

    while (true)
    {
        if (rt != null)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            tex.Apply();

            RenderTexture.active = prev;

            var c = tex.GetPixel(0, 0);
            Debug.Log($"[RT PROBE] pixel(0,0)=({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})");
        }

        yield return wait;
    }
}

private void HandleWS(string msg)
{
    var j = JsonUtility.FromJson<Sig>(msg);
    if (j == null) return;

    if (j.type == "answer")
    {
        var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = j.sdp };
        StartCoroutine(SetRemote(desc));
    }
    else if (j.type == "candidate" && !string.IsNullOrEmpty(j.candidate))
    {
        var init = new RTCIceCandidateInit
        {
            candidate = j.candidate,
            sdpMid = j.sdpMid,
            sdpMLineIndex = j.sdpMLineIndex
        };

        if (!remoteDescSet)
        {
            pendingRemoteCandidates.Add(init);
            return;
        }

        pc.AddIceCandidate(new RTCIceCandidate(init));
    }
}

private IEnumerator SetRemote(RTCSessionDescription desc)
{
    var op = pc.SetRemoteDescription(ref desc);
    yield return op;

    remoteDescSet = true;

    foreach (var c in pendingRemoteCandidates)
        pc.AddIceCandidate(new RTCIceCandidate(c));

    pendingRemoteCandidates.Clear();

    Debug.Log(":white_check_mark: RemoteDescription set");
}

private void SendWS(Sig m)
{
    if (ws == null || ws.State != WebSocketState.Open) return;
    ws.SendText(JsonUtility.ToJson(m));
}

private void OnDestroy()
{
    try
    {
        if (wsPumpCoroutine != null) StopCoroutine(wsPumpCoroutine);
        if (statsCoroutine != null) StopCoroutine(statsCoroutine);
        if (rtProbeCoroutine != null) StopCoroutine(rtProbeCoroutine);

        if (videoTrack != null)
        {
            try { videoTrack.Dispose(); } catch { }
            videoTrack = null;
        }

        if (pc != null)
        {
            try { pc.Close(); } catch { }
            try { pc.Dispose(); } catch { }
            pc = null;
        }

        if (ws != null)
        {
            _ = ws.Close();
            ws = null;
        }

        if (rt != null)
        {
            try { rt.Release(); } catch { }
            Destroy(rt);
            rt = null;
        }

        if (captureCam != null && captureCam != MocopiCamera)
        {
            Destroy(captureCam.gameObject);
        }
    }
    catch { }
}

[Serializable]
private class Sig
{
    public string role;
    public string type;
    public string sdp;
    public string candidate;
    public string sdpMid;
    public int? sdpMLineIndex;
}

}
