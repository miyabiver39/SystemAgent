// サーバーから渡されたストリームをファイルとしてブラウザに保存させる（Blazor Server の DotNetStreamReference）。
window.systemAgentDownload = async (fileName, streamRef) => {
    const buffer = await streamRef.arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer]));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
};
