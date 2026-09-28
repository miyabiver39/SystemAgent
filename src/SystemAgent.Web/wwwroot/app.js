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

// イメージアーカイブのドラッグ＆ドロップ／ファイル選択と、ブラウザからの直接アップロード（ImageDropZone.razor）。
// ファイルはブラウザ内に保持し、アップロード時にサーバーが発行した使い捨てURLへ本文そのまま（octet-stream）で送る。
window.systemAgentDropZone = {
    init(zone, input, dotnet) {
        this.destroy(zone, input);
        zone._saFiles = [];
        const add = list => {
            if (zone.classList.contains('disabled') || list.length === 0) return;
            const files = [...list];
            zone._saFiles.push(...files);
            dotnet.invokeMethodAsync('OnFilesAdded', files.map(f => ({ name: f.name, size: f.size })));
        };
        // destroy で外せるよう、登録したリスナーを要素に覚えておく
        const zoneListeners = {
            dragover: e => { e.preventDefault(); zone.classList.add('dragging'); },
            dragleave: () => zone.classList.remove('dragging'),
            drop: e => { e.preventDefault(); zone.classList.remove('dragging'); add(e.dataTransfer.files); },
            click: () => { if (!zone.classList.contains('disabled')) input.click(); },
            keydown: e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); zone.click(); } },
        };
        const inputListeners = { change: () => { add(input.files); input.value = ''; } };
        for (const [type, listener] of Object.entries(zoneListeners)) zone.addEventListener(type, listener);
        for (const [type, listener] of Object.entries(inputListeners)) input.addEventListener(type, listener);
        zone._saListeners = zoneListeners;
        input._saListeners = inputListeners;
    },
    clear(zone) {
        if (zone._saFiles) zone._saFiles.length = 0;
    },
    // コンポーネントの破棄時に呼ぶ。リスナーと保持中のファイル（.NET側への参照を含むクロージャ）を解放する
    destroy(zone, input) {
        for (const element of [zone, input]) {
            if (!element?._saListeners) continue;
            for (const [type, listener] of Object.entries(element._saListeners)) element.removeEventListener(type, listener);
            delete element._saListeners;
        }
        if (zone) delete zone._saFiles;
    },
    // 完了で取り込み結果（ランタイムの出力）を返す。失敗時はサーバーのエラー内容で reject する
    upload(zone, index, url, dotnet) {
        return new Promise((resolve, reject) => {
            const file = zone._saFiles[index];
            const xhr = new XMLHttpRequest();
            xhr.open('POST', url);
            xhr.setRequestHeader('Content-Type', 'application/octet-stream');
            xhr.setRequestHeader('X-File-Name', encodeURIComponent(file.name));
            let last = 0;
            xhr.upload.onprogress = e => {
                const now = Date.now();
                if (now - last > 300 || e.loaded === e.total) {
                    last = now;
                    dotnet.invokeMethodAsync('OnUploadProgress', index, e.loaded);
                }
            };
            xhr.onload = () => {
                let body = null;
                try { body = JSON.parse(xhr.responseText); } catch { }
                if (xhr.status >= 200 && xhr.status < 300) resolve(body?.output ?? '');
                else reject(new Error(body?.detail ?? body?.title ?? `HTTP ${xhr.status}`));
            };
            xhr.onerror = () => reject(new Error('通信エラーでアップロードできませんでした。'));
            xhr.send(file);
        });
    }
};
