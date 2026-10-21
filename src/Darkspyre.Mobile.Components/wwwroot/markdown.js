// Colours each code block under element that is not coloured yet and whose language-<name> class names a language
// highlight.js knows. A block in an unknown language, or one highlight.js fails on, stays plain text.
function highlightNew(element) {
    if (!element || typeof hljs === "undefined") return;
    for (const block of element.querySelectorAll("pre code")) {
        if (block.dataset.highlighted) continue;
        const language = Array.from(block.classList).find(name => name.startsWith("language-"));
        if (!language || !hljs.getLanguage(language.substring("language-".length))) continue;
        try {
            hljs.highlightElement(block);
        } catch {
            // The block keeps its plain text.
        }
    }
}
