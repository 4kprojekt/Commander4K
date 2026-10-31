using System.Text.Json;

namespace Commander4K.App.Browser;

/// <summary>
/// JavaScript run in the browser panel's page for the AI integration. Synchronous on purpose: WebView2's
/// <c>ExecuteScriptAsync</c> does not await promises, so the C# side polls. The scripts only type text (never send)
/// and read code blocks; site-agnostic (Gemini, ChatGPT, Claude, Perplexity…), no selectors of one site.
/// </summary>
public static class PageScripts
{
    /// <summary>
    /// Puts <paramref name="text"/> into the lowest visible message box; returns "ok" or "not-found". Checked against
    /// Gemini (Quill editor, which sits under an aria-hidden ancestor for signed-out users, so that is not a filter)
    /// and ChatGPT (a textarea; its send button enables, so the page registered the input).
    /// </summary>
    public static string TypeIntoChat(string text) => """
        (() => {
          const text = __TEXT__;
          const visible = e => { const r = e.getBoundingClientRect(); return r.width > 40 && r.height > 10 && getComputedStyle(e).visibility !== 'hidden'; };
          const boxes = [...document.querySelectorAll('textarea, [contenteditable="true"], [contenteditable=""], [role="textbox"]')]
            .filter(e => visible(e) && !e.disabled && !e.readOnly)
            .filter(e => !(e.isContentEditable && e.parentElement && e.parentElement.closest('[contenteditable="true"]')));
          if (boxes.length === 0) return 'not-found';
          const box = boxes.sort((a, b) => b.getBoundingClientRect().bottom - a.getBoundingClientRect().bottom)[0];
          box.focus();
          if (box.tagName === 'TEXTAREA') {
            const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
            setter.call(box, box.value ? box.value + '\n\n' + text : text);
            box.dispatchEvent(new Event('input', { bubbles: true }));
          } else {
            const range = document.createRange();
            range.selectNodeContents(box);
            range.collapse(false);
            const selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
            if (!document.execCommand('insertText', false, text)) {
              box.textContent += text;
              box.dispatchEvent(new InputEvent('input', { bubbles: true }));
            }
          }
          return 'ok';
        })()
        """.Replace("__TEXT__", JsonSerializer.Serialize(text), StringComparison.Ordinal);

    /// <summary>
    /// Every <c>pre</c> block with the nearest preceding text (walking up to 6 ancestors, 4 siblings each, stopping at
    /// another code block) — that text should contain the "File: path" line the plugin prompt asks for.
    /// </summary>
    public const string ReadCodeBlocks = """
        (() => {
          const fileLine = /(File|Plik|Filename)\s*:?/i;
          const labelOf = pre => {
            let node = pre;
            for (let depth = 0; depth < 6 && node; depth++) {
              let sibling = node.previousElementSibling;
              for (let k = 0; k < 4 && sibling; k++) {
                const text = (sibling.innerText || '').trim();
                if (fileLine.test(text)) return text.split('\n').slice(-3).join('\n');
                if (sibling.querySelector && sibling.querySelector('pre')) return null;
                sibling = sibling.previousElementSibling;
              }
              node = node.parentElement;
            }
            return null;
          };
          return [...document.querySelectorAll('pre')].map(pre => ({ label: labelOf(pre), code: (pre.querySelector('code') || pre).innerText }));
        })()
        """;
}
