"use strict";

const editor = document.getElementById("editor");
const previewFrame = document.getElementById("preview");
const previewSubject = document.getElementById("preview-subject");
const notice = document.getElementById("notice");
const emailTags = new Set(["A", "ABBR", "ADDRESS", "B", "BIG", "BLOCKQUOTE", "BR", "CAPTION", "CENTER", "CITE", "CODE", "COL", "COLGROUP", "DD", "DEL", "DIV", "DL", "DT", "EM", "FONT", "H1", "H2", "H3", "H4", "H5", "H6", "HR", "I", "IMG", "INS", "LI", "OL", "P", "PRE", "S", "SMALL", "SPAN", "STRIKE", "STRONG", "SUB", "SUP", "TABLE", "TBODY", "TD", "TFOOT", "TH", "THEAD", "TR", "TT", "U", "UL"]);
const blockedTags = new Set(["SCRIPT", "STYLE", "LINK", "META", "TITLE", "BASE", "IFRAME", "FRAME", "FRAMESET", "OBJECT", "EMBED", "APPLET", "FORM", "INPUT", "BUTTON", "SELECT", "OPTION", "TEXTAREA", "SVG", "MATH", "VIDEO", "AUDIO", "SOURCE", "TRACK", "CANVAS", "TEMPLATE"]);
const emailStyles = new Set(["font-family", "font-size", "font-weight", "font-style", "font-variant", "line-height", "text-decoration", "text-decoration-line", "text-decoration-color", "text-align", "text-indent", "text-transform", "color", "background-color", "vertical-align", "white-space", "border", "border-top", "border-right", "border-bottom", "border-left", "border-color", "border-style", "border-width", "border-collapse", "border-spacing", "padding", "padding-top", "padding-right", "padding-bottom", "padding-left", "margin", "margin-top", "margin-right", "margin-bottom", "margin-left", "width", "height", "min-width", "max-width", "table-layout", "list-style-type", "direction", "word-break", "overflow-wrap"]);
const editorCommands = new Set(["bold", "italic", "underline", "strikethrough", "subscript", "superscript", "fontname", "forecolor", "backcolor", "hilitecolor", "justifyleft", "justifycenter", "justifyright", "justifyfull", "insertorderedlist", "insertunorderedlist", "indent", "outdent", "createlink", "unlink", "removeformat", "undo", "redo", "selectall", "delete", "forwarddelete", "inserthorizontalrule", "formatblock"]);
const dateExpressionPattern = /\{\{date(?::[^{}\r\n]+)?\}\}|\{date(?::[^{}\r\n]+)?\}/g;
let images = new Map();
let savedSelection = null;
let pendingFontSize = null;
let changeTimer = null;
let lastPreviewHtml = null;

function sanitizeMessage(html) {
    const parsed = new DOMParser().parseFromString(html, "text/html");
    let blocked = parsed.head.querySelector("script, style, link, base") !== null;
    if (parsed.body.attributes.length > 0) {
        const body = parsed.createElement("div");
        for (const attribute of parsed.body.attributes)
            body.setAttribute(attribute.name, attribute.value);
        body.append(...parsed.body.childNodes);
        parsed.body.replaceChildren(body);
        for (const attribute of [...parsed.body.attributes])
            parsed.body.removeAttribute(attribute.name);
    }
    for (const element of parsed.body.querySelectorAll("*")) {
        if (blockedTags.has(element.tagName)) {
            element.remove();
            blocked = true;
            continue;
        }
        if (!emailTags.has(element.tagName)) {
            element.replaceWith(...element.childNodes);
            continue;
        }
        const attributes = [...element.attributes];
        for (const attribute of attributes)
            element.removeAttribute(attribute.name);
        for (const attribute of attributes) {
            const name = attribute.name.toLowerCase();
            const value = attribute.value.trim();
            if (name === "style") {
                const styles = parsed.createElement("span").style;
                styles.cssText = value;
                for (const property of styles) {
                    const setting = styles.getPropertyValue(property);
                    if (emailStyles.has(property) && !/(?:url|image|expression|var|attr)\s*\(|[\\<>]/i.test(setting))
                        element.style.setProperty(property, setting);
                    else
                        blocked = true;
                }
            } else if (name === "title" || name === "alt" && element.tagName === "IMG") {
                element.setAttribute(name, attribute.value);
            } else if (name === "bgcolor" && CSS.supports("background-color", value)) {
                element.style.backgroundColor = value;
            } else if (name === "dir" && /^(?:ltr|rtl|auto)$/i.test(value) || name === "lang" && /^[a-z0-9-]+$/i.test(value)) {
                element.setAttribute(name, value);
            } else if (name === "align" && /^(?:left|right|center|justify)$/i.test(value) || name === "valign" && /^(?:top|middle|bottom|baseline)$/i.test(value)) {
                element.setAttribute(name, value);
            } else if (["width", "height"].includes(name) && /^\d+(?:\.\d+)?(?:px|%)?$/i.test(value)) {
                element.setAttribute(name, value);
            } else if (["border", "cellpadding", "cellspacing", "colspan", "rowspan", "span", "start", "value"].includes(name) && /^-?\d+$/.test(value)) {
                element.setAttribute(name, value);
            } else if (element.tagName === "A" && name === "href" && /^(?:https?:|mailto:|tel:)/i.test(value)) {
                element.setAttribute(name, value);
            } else if (element.tagName === "FONT" && name === "face" && /^[\w ,"'-]{1,200}$/.test(value)) {
                element.style.fontFamily = value;
            } else if (element.tagName === "FONT" && name === "color" && CSS.supports("color", value)) {
                element.style.color = value;
            } else if (element.tagName === "FONT" && name === "size" && /^[1-7]$/.test(value)) {
                element.style.fontSize = ["8pt", "10pt", "12pt", "14pt", "18pt", "24pt", "36pt"][Number(value) - 1];
            } else if (name.startsWith("on") || ["srcset", "background", "contenteditable", "formaction"].includes(name)) {
                blocked = true;
            }
        }
        if (element.tagName === "IMG") {
            const source = attributes.find(attribute => attribute.name.toLowerCase() === "src")?.value.trim() || "";
            const image = source.toLowerCase().startsWith("cid:")
                ? images.get(source.slice(4))
                : [...images.values()].find(candidate => candidate.url === source);
            if (!image) {
                element.replaceWith(parsed.createTextNode(element.getAttribute("alt") || ""));
                blocked = true;
            } else {
                element.src = image.url;
                element.dataset.imageId = image.id;
                if (!element.style.maxWidth)
                    element.style.maxWidth = "100%";
            }
        }
    }
    return { html: parsed.body.innerHTML, blocked };
}

function findDateExpressions(container) {
    const walker = document.createTreeWalker(container, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
    const segments = [];
    let text = "";
    let block = null;
    while (walker.nextNode()) {
        const node = walker.currentNode;
        if (node.nodeType === Node.ELEMENT_NODE) {
            if (node.matches("br, img, hr, [data-dateexpression]"))
                text += "\n";
            continue;
        }
        if (node.parentElement.closest("[data-dateexpression]"))
            continue;
        const owner = node.parentElement.closest("address, blockquote, caption, dd, div, dt, h1, h2, h3, h4, h5, h6, li, p, pre, td, th");
        if (owner !== block)
            text += "\n";
        block = owner;
        segments.push({ node, start: text.length, end: text.length + node.data.length });
        text += node.data;
    }
    return [...text.matchAll(dateExpressionPattern)].map(match => {
        const start = segments.find(segment => segment.start <= match.index && segment.end > match.index);
        const endOffset = match.index + match[0].length;
        const end = segments.find(segment => segment.start < endOffset && segment.end >= endOffset);
        const range = document.createRange();
        range.setStart(start.node, match.index - start.start);
        range.setEnd(end.node, endOffset - end.start);
        return { text: match[0], range };
    });
}

function protectDatePlaceholders(container) {
    for (const expression of findDateExpressions(container).reverse()) {
        const token = document.createElement("span");
        token.dataset.dateexpression = expression.text;
        token.contentEditable = "false";
        token.draggable = false;
        token.setAttribute("role", "img");
        token.setAttribute("aria-label", "Date placeholder " + expression.text);
        token.textContent = expression.text;
        expression.range.deleteContents();
        expression.range.insertNode(token);
    }
}

function selectDateExpressions() {
    const selection = window.getSelection();
    const range = selection.getRangeAt(0);
    for (const expression of findDateExpressions(editor)) {
        const start = range.cloneRange();
        start.collapse(true);
        const end = range.cloneRange();
        end.collapse(false);
        const expressionStart = expression.range.cloneRange();
        expressionStart.collapse(true);
        const expressionEnd = expression.range.cloneRange();
        expressionEnd.collapse(false);
        if (start.compareBoundaryPoints(Range.START_TO_START, expressionEnd) >= 0 || end.compareBoundaryPoints(Range.START_TO_START, expressionStart) <= 0)
            continue;
        if (range.compareBoundaryPoints(Range.START_TO_START, expression.range) > 0)
            range.setStart(expression.range.startContainer, expression.range.startOffset);
        if (range.compareBoundaryPoints(Range.END_TO_END, expression.range) < 0)
            range.setEnd(expression.range.endContainer, expression.range.endOffset);
    }
    selection.removeAllRanges();
    selection.addRange(range);
    savedSelection = range.cloneRange();
}

function restoreEditorSelection() {
    editor.focus({ preventScroll: true });
    const selection = window.getSelection();
    selection.removeAllRanges();
    if (savedSelection && editor.contains(savedSelection.commonAncestorContainer)) {
        selection.addRange(savedSelection);
    } else {
        const range = document.createRange();
        range.selectNodeContents(editor);
        range.collapse(false);
        selection.addRange(range);
    }
}

function reportBlockedContent() {
    const message = "Unsupported message content was removed. Add photos with Add photo or paste an image directly.";
    notice.textContent = message;
    notice.hidden = false;
    window.chrome.webview.postMessage({ type: "notice", message });
}

function reportSelectionFormat() {
    const selection = window.getSelection();
    if (selection.rangeCount === 0 || !editor.contains(selection.getRangeAt(0).commonAncestorContainer))
        return;
    const anchor = selection.anchorNode.nodeType === Node.ELEMENT_NODE ? selection.anchorNode : selection.anchorNode.parentElement;
    const styles = getComputedStyle(anchor);
    const range = selection.getRangeAt(0);
    let fontName = styles.fontFamily.split(",")[0].trim().replace(/^['"]|['"]$/g, "");
    let fontSize = range.collapsed && pendingFontSize !== null ? pendingFontSize : Math.round(parseFloat(styles.fontSize) * 75) / 100;
    if (!range.collapsed && range.commonAncestorContainer.nodeType !== Node.TEXT_NODE) {
        const walker = document.createTreeWalker(range.commonAncestorContainer, NodeFilter.SHOW_TEXT);
        let selectedFamily = null;
        let selectedSize = null;
        while (walker.nextNode()) {
            const text = walker.currentNode;
            if (text.length === 0 || !range.intersectsNode(text)
                || text === range.startContainer && range.startOffset === text.length
                || text === range.endContainer && range.endOffset === 0)
                continue;
            const textStyles = getComputedStyle(text.parentElement);
            const family = textStyles.fontFamily.toLowerCase();
            const size = Math.round(parseFloat(textStyles.fontSize) * 75) / 100;
            if (selectedFamily === null) {
                selectedFamily = family;
                selectedSize = size;
                fontName = textStyles.fontFamily.split(",")[0].trim().replace(/^['"]|['"]$/g, "");
                fontSize = size;
            } else {
                if (family !== selectedFamily)
                    fontName = "";
                if (size !== selectedSize)
                    fontSize = 0;
                if (fontName === "" && fontSize === 0)
                    break;
            }
        }
    }
    window.chrome.webview.postMessage({
        type: "format",
        bold: document.queryCommandState("bold"),
        italic: document.queryCommandState("italic"),
        underline: document.queryCommandState("underline"),
        fontName,
        fontSize
    });
}

async function importClipboardImages(files) {
    for (const file of files) {
        if (!/^image\/(?:png|jpeg|gif|bmp)$/i.test(file.type)) {
            reportBlockedContent();
            continue;
        }
        const base64 = await new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve(reader.result.slice(reader.result.indexOf(",") + 1));
            reader.onerror = () => reject(reader.error);
            reader.readAsDataURL(file);
        });
        window.chrome.webview.postMessage({ type: "image", name: file.name, base64 });
    }
}

window.composer = {
    load(data) {
        clearTimeout(changeTimer);
        pendingFontSize = null;
        lastPreviewHtml = null;
        images = new Map(data.images.filter(image => {
            const url = new URL(image.url, location.origin);
            return url.origin === location.origin && /^\/images\/[^/]+$/.test(url.pathname) && !url.search && !url.hash;
        }).map(image => [image.id, { id: image.id, url: new URL(image.url, location.origin).href }]));
        const parsed = new DOMParser().parseFromString(data.html, "text/html");
        const wrapper = parsed.body.firstElementChild;
        let html = data.html;
        if (wrapper?.dataset.outlookAutomailBody === "true" && [...parsed.body.childNodes].every(node => node === wrapper || node.nodeType === Node.TEXT_NODE && !node.textContent.trim())) {
            wrapper.replaceWith(...wrapper.childNodes);
            html = parsed.body.innerHTML;
        }
        const sanitized = sanitizeMessage(html);
        editor.innerHTML = sanitized.html;
        protectDatePlaceholders(editor);
        savedSelection = null;
        notice.hidden = true;
        if (sanitized.blocked)
            reportBlockedContent();
        this.preview({ subject: data.subject, html: data.previewHtml, images: data.images });
        return this.exportBody();
    },

    preview(data) {
        if (data.images) {
            images = new Map(data.images.filter(image => {
                const url = new URL(image.url, location.origin);
                return url.origin === location.origin && /^\/images\/[^/]+$/.test(url.pathname) && !url.search && !url.hash;
            }).map(image => [image.id, { id: image.id, url: new URL(image.url, location.origin).href }]));
        }
        previewSubject.textContent = data.subject;
        const sanitized = sanitizeMessage(data.html);
        if (lastPreviewHtml === sanitized.html)
            return;
        lastPreviewHtml = sanitized.html;
        previewFrame.srcdoc = '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src https://composer.outlookautomail.invalid \'unsafe-inline\'; img-src https://composer.outlookautomail.invalid; base-uri \'none\'; form-action \'none\'"><link rel="stylesheet" href="https://composer.outlookautomail.invalid/MessageComposer.css"></head><body class="preview-document email-paper">' + sanitized.html + "</body></html>";
    },

    command(name, value) {
        const command = name.toLowerCase();
        if (command !== "fontsize" && !editorCommands.has(command))
            return false;
        if (command === "createlink" && !/^(?:https?:|mailto:|tel:)/i.test(value))
            return false;
        restoreEditorSelection();
        if (!["undo", "redo", "selectall", "delete", "forwarddelete", "inserthorizontalrule"].includes(command))
            selectDateExpressions();
        let result;
        if (command === "fontsize") {
            const points = Number(value);
            if (!Number.isFinite(points) || points < 6 || points > 96)
                return false;
            pendingFontSize = points;
            result = document.execCommand("fontSize", false, "7");
            for (const font of editor.querySelectorAll('font[size="7"]')) {
                font.removeAttribute("size");
                font.style.fontSize = points + "pt";
            }
        } else {
            if (command === "undo" || command === "redo")
                pendingFontSize = null;
            result = document.execCommand(name, false, value ?? null);
        }
        clearTimeout(changeTimer);
        window.chrome.webview.postMessage({ type: "changed", html: this.exportBody() });
        reportSelectionFormat();
        return result;
    },

    insertText(text) {
        restoreEditorSelection();
        const fragment = document.createElement("div");
        fragment.textContent = text;
        protectDatePlaceholders(fragment);
        document.execCommand("insertHTML", false, fragment.innerHTML.replace(/\r\n|\r|\n/g, "<br>"));
        clearTimeout(changeTimer);
        window.chrome.webview.postMessage({ type: "changed", html: this.exportBody() });
    },

    insertImage(id) {
        const image = images.get(id);
        if (!image)
            return false;
        restoreEditorSelection();
        const element = document.createElement("img");
        element.src = image.url;
        element.dataset.imageId = id;
        element.alt = "Photo";
        document.execCommand("insertHTML", false, element.outerHTML);
        clearTimeout(changeTimer);
        window.chrome.webview.postMessage({ type: "changed", html: this.exportBody() });
        return true;
    },

    removeImage(id) {
        for (const image of editor.querySelectorAll("img[data-image-id]")) {
            if (image.dataset.imageId !== id)
                continue;
            const range = document.createRange();
            range.selectNode(image);
            savedSelection = range;
            restoreEditorSelection();
            document.execCommand("delete");
        }
        clearTimeout(changeTimer);
        window.chrome.webview.postMessage({ type: "changed", html: this.exportBody() });
    },

    exportBody() {
        const body = editor.cloneNode(true);
        for (const token of body.querySelectorAll("[data-dateexpression]"))
            token.replaceWith(document.createTextNode(token.dataset.dateexpression));
        for (const expression of findDateExpressions(body).reverse()) {
            expression.range.deleteContents();
            expression.range.insertNode(document.createTextNode(expression.text));
        }
        for (const image of body.querySelectorAll("img[data-image-id]")) {
            image.setAttribute("src", "cid:" + image.dataset.imageId);
            image.removeAttribute("data-image-id");
            if (!image.style.maxWidth)
                image.style.maxWidth = "100%";
        }
        const wrapper = document.createElement("div");
        wrapper.dataset.outlookAutomailBody = "true";
        const theme = getComputedStyle(editor);
        wrapper.style.fontFamily = theme.getPropertyValue("--message-font-family").trim();
        wrapper.style.fontSize = theme.getPropertyValue("--message-font-size").trim();
        wrapper.style.color = theme.getPropertyValue("--message-color").trim();
        wrapper.style.lineHeight = theme.getPropertyValue("--message-line-height").trim();
        wrapper.style.overflowWrap = "break-word";
        wrapper.append(...body.childNodes);
        return wrapper.outerHTML;
    }
};

document.addEventListener("selectionchange", () => {
    const selection = window.getSelection();
    if (selection.rangeCount > 0 && editor.contains(selection.getRangeAt(0).commonAncestorContainer))
        savedSelection = selection.getRangeAt(0).cloneRange();
    reportSelectionFormat();
});

editor.addEventListener("input", () => {
    if (pendingFontSize !== null) {
        for (const font of editor.querySelectorAll('font[size="7"]')) {
            font.removeAttribute("size");
            font.style.fontSize = pendingFontSize + "pt";
        }
    }
    clearTimeout(changeTimer);
    changeTimer = setTimeout(() => {
        window.chrome.webview.postMessage({ type: "changed", html: window.composer.exportBody() });
    }, 250);
    reportSelectionFormat();
});

editor.addEventListener("pointerdown", () => {
    pendingFontSize = null;
});

editor.addEventListener("keydown", event => {
    if (["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "PageUp", "PageDown", "Tab"].includes(event.key) || event.ctrlKey && event.key.toLowerCase() === "a")
        pendingFontSize = null;
    if (!event.ctrlKey || event.altKey || event.shiftKey)
        return;
    const command = { b: "bold", i: "italic", u: "underline" }[event.key.toLowerCase()];
    if (!command)
        return;
    event.preventDefault();
    savedSelection = window.getSelection().getRangeAt(0).cloneRange();
    window.composer.command(command);
});

editor.addEventListener("paste", async event => {
    event.preventDefault();
    const files = [...event.clipboardData.files];
    if (files.length > 0) {
        await importClipboardImages(files);
        return;
    }
    const html = event.clipboardData.getData("text/html");
    if (html) {
        const sanitized = sanitizeMessage(html);
        const fragment = document.createElement("div");
        fragment.innerHTML = sanitized.html;
        protectDatePlaceholders(fragment);
        document.execCommand("insertHTML", false, fragment.innerHTML);
        if (sanitized.blocked)
            reportBlockedContent();
    } else {
        window.composer.insertText(event.clipboardData.getData("text/plain"));
    }
});

editor.addEventListener("dragover", event => {
    event.preventDefault();
    event.dataTransfer.dropEffect = event.dataTransfer.types.includes("Files") ? "copy" : "none";
});

editor.addEventListener("drop", async event => {
    event.preventDefault();
    const range = document.caretRangeFromPoint(event.clientX, event.clientY);
    if (range && editor.contains(range.commonAncestorContainer))
        savedSelection = range;
    await importClipboardImages([...event.dataTransfer.files]);
});

editor.addEventListener("click", event => {
    if (event.target.closest("a"))
        event.preventDefault();
});

window.chrome.webview.postMessage({ type: "ready" });
