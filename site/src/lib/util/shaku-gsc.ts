import shakuTransformer from "shaku-code-annotate-shiki-transformer";
import type { ShikiTransformer } from "shiki";

const GSC_LANGS = new Set(["gsc", "csc", "gsh"]);

export function shakuGscTransformer(): ShikiTransformer {
    const inner = shakuTransformer();
    return {
        ...inner,
        tokens(lines) {
            const realLang = this.options.lang;
            if (GSC_LANGS.has(realLang)) {
                this.options.lang = "c";
            }
            inner.tokens!.call(this, lines);
            this.options.lang = realLang;
        },
    };
}
