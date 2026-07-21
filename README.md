# WebRTC Signaling Server for AIRR Remote Display

卒業研究で制作した、Unity映像をブラウザへリアルタイム配信し、AIRRによる空中像として表示するための通信システムです。

本システムでは、Unity側とブラウザ側の接続情報をWebSocketで中継し、WebRTCによる映像配信を成立させます。ブラウザで受信した映像をタブレット端末に表示し、AIRR筐体内に設置することで、遠隔地のUnity映像を空中像として観察できる環境を構築しました。

## 制作背景

既存の画面共有アプリではなく自作構成にした理由は、将来的に空中表示された映像に対して、観察者側から操作や反応を加えるインタラクション機能への拡張を見据えたためです。

## システム構成

Unityのカメラ映像をRenderTextureへ描画し、WebRTCの映像トラックとしてブラウザへ送信します。

Unity側とブラウザ側は、WebSocketを用いたシグナリングサーバを通じて、Offer、Answer、ICE Candidateなどの接続情報を交換します。WebRTC接続の確立後、ブラウザ側で受信した映像をvideo要素に表示します。

映像送信の流れは以下の通りです。

Unity Camera  
→ RenderTexture  
→ WebRTC VideoStreamTrack  
→ Browser  
→ Tablet  
→ AIRR

## 主なファイル

- `server.js`  
  Unity側とブラウザ側の間で、WebRTC接続に必要な情報を中継するシグナリングサーバです。

- `public/index.html`  
  Unityから送信されたWebRTC映像を受信し、ブラウザ上に表示するページです。

- `unity-client/WebRTC_RenderTexture_Streamer.cs`  
  Unityのカメラ映像をRenderTextureへ描画し、WebRTCの映像トラックとしてブラウザへ送信するUnity側のスクリプトです。

## 担当範囲

- Unity映像をブラウザへ配信する通信構成の検討
- WebSocketを用いたシグナリングサーバの構築
- ブラウザ側のWebRTC受信ページの作成
- Unity側への映像送信処理の組み込み
- Unity側とブラウザ側の接続確認
- 同一LANおよび異なるネットワーク環境での動作検証
- タブレット端末とAIRR筐体を用いた空中像表示の検証
- 通信ログやWebRTC Statsを用いた問題原因の切り分け

## 使用技術

- Unity / C#
- WebRTC
- WebSocket
- Node.js
- Express
- ws
- STUN / TURN
- HTML
- JavaScript

## 表示方式・機器

- AIRR
- タブレット端末

## 動作動画

Unity映像をWebRTCでブラウザへリアルタイム配信し、タブレット端末とAIRR筐体を用いて空中像として表示した様子です。

[YouTubeでデモ動画を見る](https://www.youtube.com/watch?v=QVifiQyBw4A)

## 注意事項

本リポジトリは、研究で使用した通信システム部分のソースコードを公開したものです。

セキュリティ上、実際に使用したTURNサーバのユーザー名、認証情報、一部の接続情報は、以下のような公開用の値に置き換えています。

- `YOUR_TURN_USERNAME`
- `YOUR_TURN_CREDENTIAL`

そのため、異なるネットワーク間で動作させる場合は、利用者自身のTURNサーバ情報を設定する必要があります。
