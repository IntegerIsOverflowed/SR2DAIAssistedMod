using System;
using System.Collections.Generic;

namespace Sr2d64CSport
{
    /// <summary>
    /// Static tables for the font and image decoders: the PDF / PostScript text encodings (code -> glyph name), the Adobe
    /// Glyph List subset (glyph name -> Unicode, for looking glyphs up in TrueType cmaps), the CFF standard strings, the
    /// Macintosh TrueType glyph order (post table format 1 / 2) and the CCITT Group 3 / 4 run-length code tables.
    /// Generated from the pdf.js sources (Apache 2.0); kept as compact strings and expanded on first use.
    /// </summary>
    internal static class VectorFontData
    {
        static string[] Split256(string s) { var a = s.Split(' '); for (int i = 0; i < a.Length; i++) if (a[i] == ".") a[i] = ""; return a; }
        static string[]? std, win, mac, sym, zapf, macExpert, cffStd, macGlyphs;
        public static string[] StandardEncoding => std ??= Split256(StdSrc);
        public static string[] WinAnsiEncoding => win ??= Split256(WinSrc);
        public static string[] MacRomanEncoding => mac ??= Split256(MacSrc);
        public static string[] SymbolEncoding => sym ??= Split256(SymSrc);
        public static string[] ZapfDingbatsEncoding => zapf ??= Split256(ZapfSrc);
        public static string[] MacExpertEncoding => macExpert ??= Split256(MacExpertSrc);
        /// <summary>The 391 CFF standard strings (SID -> name).</summary>
        public static string[] CffStandardStrings => cffStd ??= CffSrc.Split(' ');
        /// <summary>The 258 standard Macintosh glyph names (TrueType post table).</summary>
        public static string[] MacGlyphNames => macGlyphs ??= MacGlyphSrc.Split(' ');
        public static string[]? EncodingByName(string name) => name switch
        {
            "WinAnsiEncoding" => WinAnsiEncoding, "MacRomanEncoding" => MacRomanEncoding, "StandardEncoding" => StandardEncoding,
            "MacExpertEncoding" => MacExpertEncoding, "Symbol" => SymbolEncoding, "ZapfDingbats" => ZapfDingbatsEncoding, _ => null
        };

        static Dictionary<string, int>? glyphs; static Dictionary<int, string>? glyphsRev;
        /// <summary>Glyph name -> Unicode code point (Adobe Glyph List subset + uniXXXX / uXXXX[X] / gXX forms handled by <see cref="GlyphToUnicode"/>).</summary>
        public static Dictionary<string, int> GlyphList
        {
            get
            {
                if (glyphs != null) return glyphs;
                var d = new Dictionary<string, int>(4096, StringComparer.Ordinal);
                foreach (var tok in GlyphSrc.Split(' ')) { int c = tok.IndexOf(':'); if (c > 0) d[tok.Substring(0, c)] = System.Convert.ToInt32(tok.Substring(c + 1), 16); }
                return glyphs = d;
            }
        }
        /// <summary>Unicode -> a glyph name (first AGL name that maps to it).</summary>
        public static Dictionary<int, string> GlyphListReverse
        {
            get
            {
                if (glyphsRev != null) return glyphsRev;
                var d = new Dictionary<int, string>(4096);
                foreach (var kv in GlyphList) d.TryAdd(kv.Value, kv.Key);
                return glyphsRev = d;
            }
        }
        /// <summary>Glyph name -> Unicode: AGL, "uniXXXX", "uXXXX[XX]", names with a suffix ("a.sc" -> "a"), "Cdd" / "Gdd" style names return -1.</summary>
        public static int GlyphToUnicode(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            if (GlyphList.TryGetValue(name, out int u)) return u;
            int dot = name.IndexOf('.'); if (dot > 0) return GlyphToUnicode(name.Substring(0, dot));
            if (name.Length >= 7 && name.StartsWith("uni", StringComparison.Ordinal) && int.TryParse(name.AsSpan(3, 4), System.Globalization.NumberStyles.HexNumber, null, out int h)) return h;
            if (name.Length >= 5 && name.Length <= 7 && name[0] == 'u' && int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int h2)) return h2;
            if (name.Length == 1) return name[0];
            return -1;
        }

        // ---- CCITT: "bits,code,run" triples (white / black terminating + make-up codes, common extended make-up codes included in both)
        static Dictionary<int, int>? ccW, ccB;
        static Dictionary<int, int> CcTable(string src)
        {
            var d = new Dictionary<int, int>(128);
            foreach (var t in src.Split(", ")) { var p = t.Split(','); int bits = int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), code = System.Convert.ToInt32(p[1], 16), run = int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture); d[bits << 16 | code] = run; }
            return d;
        }
        /// <summary>Key = bits &lt;&lt; 16 | code; value = run length (-2 = EOL).</summary>
        public static Dictionary<int, int> CcittWhite => ccW ??= CcTable(CcWhiteSrc);
        public static Dictionary<int, int> CcittBlack => ccB ??= CcTable(CcBlackSrc);

        const string StdSrc =
            ". . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . space exclam quotedbl numbersign dollar percent ampersand quoteright parenleft " +
            "parenright asterisk plus comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal greater question at A " +
            "B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright asciicircum underscore quoteleft a b c d e f g h i j k l m n o p " +
            "q r s t u v w x y z braceleft bar braceright asciitilde . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . exclamdown cent sterling " +
            "fraction yen florin section currency quotesingle quotedblleft guillemotleft guilsinglleft guilsinglright fi fl . endash dagger daggerdbl " +
            "periodcentered . paragraph bullet quotesinglbase quotedblbase quotedblright guillemotright ellipsis perthousand . questiondown . grave acute " +
            "circumflex tilde macron breve dotaccent dieresis . ring cedilla . hungarumlaut ogonek caron emdash . . . . . . . . . . . . . . . . AE . ordfeminine . " +
            ". . . Lslash Oslash OE ordmasculine . . . . . ae . . . dotlessi . . lslash oslash oe germandbls . . . .";
        const string WinSrc =
            ". . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft " +
            "parenright asterisk plus comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal greater question at A " +
            "B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p q r " +
            "s t u v w x y z braceleft bar braceright asciitilde bullet Euro bullet quotesinglbase florin quotedblbase ellipsis dagger daggerdbl circumflex " +
            "perthousand Scaron guilsinglleft OE bullet Zcaron bullet bullet quoteleft quoteright quotedblleft quotedblright bullet endash emdash tilde trademark " +
            "scaron guilsinglright oe bullet zcaron Ydieresis space exclamdown cent sterling currency yen brokenbar section dieresis copyright ordfeminine " +
            "guillemotleft logicalnot hyphen registered macron degree plusminus twosuperior threesuperior acute mu paragraph periodcentered cedilla onesuperior " +
            "ordmasculine guillemotright onequarter onehalf threequarters questiondown Agrave Aacute Acircumflex Atilde Adieresis Aring AE Ccedilla Egrave Eacute " +
            "Ecircumflex Edieresis Igrave Iacute Icircumflex Idieresis Eth Ntilde Ograve Oacute Ocircumflex Otilde Odieresis multiply Oslash Ugrave Uacute " +
            "Ucircumflex Udieresis Yacute Thorn germandbls agrave aacute acircumflex atilde adieresis aring ae ccedilla egrave eacute ecircumflex edieresis igrave " +
            "iacute icircumflex idieresis eth ntilde ograve oacute ocircumflex otilde odieresis divide oslash ugrave uacute ucircumflex udieresis yacute thorn " +
            "ydieresis";
        const string MacSrc =
            ". . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft " +
            "parenright asterisk plus comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal greater question at A " +
            "B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p q r " +
            "s t u v w x y z braceleft bar braceright asciitilde . Adieresis Aring Ccedilla Eacute Ntilde Odieresis Udieresis aacute agrave acircumflex adieresis " +
            "atilde aring ccedilla eacute egrave ecircumflex edieresis iacute igrave icircumflex idieresis ntilde oacute ograve ocircumflex odieresis otilde uacute " +
            "ugrave ucircumflex udieresis dagger degree cent sterling section bullet paragraph germandbls registered copyright trademark acute dieresis notequal AE " +
            "Oslash infinity plusminus lessequal greaterequal yen mu partialdiff summation product pi integral ordfeminine ordmasculine Omega ae oslash " +
            "questiondown exclamdown logicalnot radical florin approxequal Delta guillemotleft guillemotright ellipsis space Agrave Atilde Otilde OE oe endash " +
            "emdash quotedblleft quotedblright quoteleft quoteright divide lozenge ydieresis Ydieresis fraction currency guilsinglleft guilsinglright fi fl " +
            "daggerdbl periodcentered quotesinglbase quotedblbase perthousand Acircumflex Ecircumflex Aacute Edieresis Egrave Iacute Icircumflex Idieresis Igrave " +
            "Oacute Ocircumflex apple Ograve Uacute Ucircumflex Ugrave dotlessi circumflex tilde macron breve dotaccent ring cedilla hungarumlaut ogonek caron";
        const string SymSrc =
            ". . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . space exclam universal numbersign existential percent ampersand suchthat parenleft " +
            "parenright asteriskmath plus comma minus period slash zero one two three four five six seven eight nine colon semicolon less equal greater question " +
            "congruent Alpha Beta Chi Delta Epsilon Phi Gamma Eta Iota theta1 Kappa Lambda Mu Nu Omicron Pi Theta Rho Sigma Tau Upsilon sigma1 Omega Xi Psi Zeta " +
            "bracketleft therefore bracketright perpendicular underscore radicalex alpha beta chi delta epsilon phi gamma eta iota phi1 kappa lambda mu nu omicron " +
            "pi theta rho sigma tau upsilon omega1 omega xi psi zeta braceleft bar braceright similar . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . " +
            ". . Euro Upsilon1 minute lessequal fraction infinity florin club diamond heart spade arrowboth arrowleft arrowup arrowright arrowdown degree plusminus " +
            "second greaterequal multiply proportional partialdiff bullet divide notequal equivalence approxequal ellipsis arrowvertex arrowhorizex carriagereturn " +
            "aleph Ifraktur Rfraktur weierstrass circlemultiply circleplus emptyset intersection union propersuperset reflexsuperset notsubset propersubset " +
            "reflexsubset element notelement angle gradient registerserif copyrightserif trademarkserif product radical dotmath logicalnot logicaland logicalor " +
            "arrowdblboth arrowdblleft arrowdblup arrowdblright arrowdbldown lozenge angleleft registersans copyrightsans trademarksans summation parenlefttp " +
            "parenleftex parenleftbt bracketlefttp bracketleftex bracketleftbt bracelefttp braceleftmid braceleftbt braceex . angleright integral integraltp " +
            "integralex integralbt parenrighttp parenrightex parenrightbt bracketrighttp bracketrightex bracketrightbt bracerighttp bracerightmid bracerightbt .";
        const string ZapfSrc =
            ". . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . space a1 a2 a202 a3 a4 a5 a119 a118 a117 a11 a12 a13 a14 a15 a16 a105 a17 a18 a19 a20 " +
            "a21 a22 a23 a24 a25 a26 a27 a28 a6 a7 a8 a9 a10 a29 a30 a31 a32 a33 a34 a35 a36 a37 a38 a39 a40 a41 a42 a43 a44 a45 a46 a47 a48 a49 a50 a51 a52 a53 " +
            "a54 a55 a56 a57 a58 a59 a60 a61 a62 a63 a64 a65 a66 a67 a68 a69 a70 a71 a72 a73 a74 a203 a75 a204 a76 a77 a78 a79 a81 a82 a83 a84 a97 a98 a99 a100 . " +
            "a89 a90 a93 a94 a91 a92 a205 a85 a206 a86 a87 a88 a95 a96 . . . . . . . . . . . . . . . . . . . a101 a102 a103 a104 a106 a107 a108 a112 a111 a110 a109 " +
            "a120 a121 a122 a123 a124 a125 a126 a127 a128 a129 a130 a131 a132 a133 a134 a135 a136 a137 a138 a139 a140 a141 a142 a143 a144 a145 a146 a147 a148 a149 " +
            "a150 a151 a152 a153 a154 a155 a156 a157 a158 a159 a160 a161 a163 a164 a196 a165 a192 a166 a167 a168 a169 a170 a171 a172 a173 a162 a174 a175 a176 a177 " +
            "a178 a179 a193 a180 a199 a181 a200 a182 . a201 a183 a184 a197 a185 a194 a198 a186 a195 a187 a188 a189 a190 a191 .";
        const string MacExpertSrc =
            ". . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . space exclamsmall Hungarumlautsmall centoldstyle dollaroldstyle dollarsuperior " +
            "ampersandsmall Acutesmall parenleftsuperior parenrightsuperior twodotenleader onedotenleader comma hyphen period fraction zerooldstyle oneoldstyle " +
            "twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle sevenoldstyle eightoldstyle nineoldstyle colon semicolon . threequartersemdash . " +
            "questionsmall . . . . Ethsmall . . onequarter onehalf threequarters oneeighth threeeighths fiveeighths seveneighths onethird twothirds . . . . . . ff " +
            "fi fl ffi ffl parenleftinferior . parenrightinferior Circumflexsmall hypheninferior Gravesmall Asmall Bsmall Csmall Dsmall Esmall Fsmall Gsmall Hsmall " +
            "Ismall Jsmall Ksmall Lsmall Msmall Nsmall Osmall Psmall Qsmall Rsmall Ssmall Tsmall Usmall Vsmall Wsmall Xsmall Ysmall Zsmall colonmonetary onefitted " +
            "rupiah Tildesmall . . asuperior centsuperior . . . . Aacutesmall Agravesmall Acircumflexsmall Adieresissmall Atildesmall Aringsmall Ccedillasmall " +
            "Eacutesmall Egravesmall Ecircumflexsmall Edieresissmall Iacutesmall Igravesmall Icircumflexsmall Idieresissmall Ntildesmall Oacutesmall Ogravesmall " +
            "Ocircumflexsmall Odieresissmall Otildesmall Uacutesmall Ugravesmall Ucircumflexsmall Udieresissmall . eightsuperior fourinferior threeinferior " +
            "sixinferior eightinferior seveninferior Scaronsmall . centinferior twoinferior . Dieresissmall . Caronsmall osuperior fiveinferior . commainferior " +
            "periodinferior Yacutesmall . dollarinferior . . Thornsmall . nineinferior zeroinferior Zcaronsmall AEsmall Oslashsmall questiondownsmall oneinferior " +
            "Lslashsmall . . . . . . Cedillasmall . . . . . OEsmall figuredash hyphensuperior . . . . exclamdownsmall . Ydieresissmall . onesuperior twosuperior " +
            "threesuperior foursuperior fivesuperior sixsuperior sevensuperior ninesuperior zerosuperior . esuperior rsuperior tsuperior . . isuperior ssuperior " +
            "dsuperior . . . . . lsuperior Ogoneksmall Brevesmall Macronsmall bsuperior nsuperior msuperior commasuperior periodsuperior Dotaccentsmall Ringsmall . " +
            ". . .";
        const string CffSrc =
            ".notdef space exclam quotedbl numbersign dollar percent ampersand quoteright parenleft parenright asterisk plus comma hyphen period slash zero one two " +
            "three four five six seven eight nine colon semicolon less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft " +
            "backslash bracketright asciicircum underscore quoteleft a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar braceright asciitilde " +
            "exclamdown cent sterling fraction yen florin section currency quotesingle quotedblleft guillemotleft guilsinglleft guilsinglright fi fl endash dagger " +
            "daggerdbl periodcentered paragraph bullet quotesinglbase quotedblbase quotedblright guillemotright ellipsis perthousand questiondown grave acute " +
            "circumflex tilde macron breve dotaccent dieresis ring cedilla hungarumlaut ogonek caron emdash AE ordfeminine Lslash Oslash OE ordmasculine ae " +
            "dotlessi lslash oslash oe germandbls onesuperior logicalnot mu trademark Eth onehalf plusminus Thorn onequarter divide brokenbar degree thorn " +
            "threequarters twosuperior registered minus eth multiply threesuperior copyright Aacute Acircumflex Adieresis Agrave Aring Atilde Ccedilla Eacute " +
            "Ecircumflex Edieresis Egrave Iacute Icircumflex Idieresis Igrave Ntilde Oacute Ocircumflex Odieresis Ograve Otilde Scaron Uacute Ucircumflex Udieresis " +
            "Ugrave Yacute Ydieresis Zcaron aacute acircumflex adieresis agrave aring atilde ccedilla eacute ecircumflex edieresis egrave iacute icircumflex " +
            "idieresis igrave ntilde oacute ocircumflex odieresis ograve otilde scaron uacute ucircumflex udieresis ugrave yacute ydieresis zcaron exclamsmall " +
            "Hungarumlautsmall dollaroldstyle dollarsuperior ampersandsmall Acutesmall parenleftsuperior parenrightsuperior twodotenleader onedotenleader " +
            "zerooldstyle oneoldstyle twooldstyle threeoldstyle fouroldstyle fiveoldstyle sixoldstyle sevenoldstyle eightoldstyle nineoldstyle commasuperior " +
            "threequartersemdash periodsuperior questionsmall asuperior bsuperior centsuperior dsuperior esuperior isuperior lsuperior msuperior nsuperior " +
            "osuperior rsuperior ssuperior tsuperior ff ffi ffl parenleftinferior parenrightinferior Circumflexsmall hyphensuperior Gravesmall Asmall Bsmall Csmall " +
            "Dsmall Esmall Fsmall Gsmall Hsmall Ismall Jsmall Ksmall Lsmall Msmall Nsmall Osmall Psmall Qsmall Rsmall Ssmall Tsmall Usmall Vsmall Wsmall Xsmall " +
            "Ysmall Zsmall colonmonetary onefitted rupiah Tildesmall exclamdownsmall centoldstyle Lslashsmall Scaronsmall Zcaronsmall Dieresissmall Brevesmall " +
            "Caronsmall Dotaccentsmall Macronsmall figuredash hypheninferior Ogoneksmall Ringsmall Cedillasmall questiondownsmall oneeighth threeeighths " +
            "fiveeighths seveneighths onethird twothirds zerosuperior foursuperior fivesuperior sixsuperior sevensuperior eightsuperior ninesuperior zeroinferior " +
            "oneinferior twoinferior threeinferior fourinferior fiveinferior sixinferior seveninferior eightinferior nineinferior centinferior dollarinferior " +
            "periodinferior commainferior Agravesmall Aacutesmall Acircumflexsmall Atildesmall Adieresissmall Aringsmall AEsmall Ccedillasmall Egravesmall " +
            "Eacutesmall Ecircumflexsmall Edieresissmall Igravesmall Iacutesmall Icircumflexsmall Idieresissmall Ethsmall Ntildesmall Ogravesmall Oacutesmall " +
            "Ocircumflexsmall Otildesmall Odieresissmall OEsmall Oslashsmall Ugravesmall Uacutesmall Ucircumflexsmall Udieresissmall Yacutesmall Thornsmall " +
            "Ydieresissmall 001.000 001.001 001.002 001.003 Black Bold Book Light Medium Regular Roman Semibold";
        const string MacGlyphSrc =
            ".notdef .null nonmarkingreturn space exclam quotedbl numbersign dollar percent ampersand quotesingle parenleft parenright asterisk plus comma hyphen " +
            "period slash zero one two three four five six seven eight nine colon semicolon less equal greater question at A B C D E F G H I J K L M N O P Q R S T " +
            "U V W X Y Z bracketleft backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar " +
            "braceright asciitilde Adieresis Aring Ccedilla Eacute Ntilde Odieresis Udieresis aacute agrave acircumflex adieresis atilde aring ccedilla eacute " +
            "egrave ecircumflex edieresis iacute igrave icircumflex idieresis ntilde oacute ograve ocircumflex odieresis otilde uacute ugrave ucircumflex udieresis " +
            "dagger degree cent sterling section bullet paragraph germandbls registered copyright trademark acute dieresis notequal AE Oslash infinity plusminus " +
            "lessequal greaterequal yen mu partialdiff summation product pi integral ordfeminine ordmasculine Omega ae oslash questiondown exclamdown logicalnot " +
            "radical florin approxequal Delta guillemotleft guillemotright ellipsis nonbreakingspace Agrave Atilde Otilde OE oe endash emdash quotedblleft " +
            "quotedblright quoteleft quoteright divide lozenge ydieresis Ydieresis fraction currency guilsinglleft guilsinglright fi fl daggerdbl periodcentered " +
            "quotesinglbase quotedblbase perthousand Acircumflex Ecircumflex Aacute Edieresis Egrave Iacute Icircumflex Idieresis Igrave Oacute Ocircumflex apple " +
            "Ograve Uacute Ucircumflex Ugrave dotlessi circumflex tilde macron breve dotaccent ring cedilla hungarumlaut ogonek caron Lslash lslash Scaron scaron " +
            "Zcaron zcaron brokenbar Eth eth Yacute yacute Thorn thorn minus multiply onesuperior twosuperior threesuperior onehalf onequarter threequarters franc " +
            "Gbreve gbreve Idotaccent Scedilla scedilla Cacute cacute Ccaron ccaron dcroat";
        const string CcWhiteSrc =
            "12,0x1,-2, 8,0x35,0, 6,0x7,1, 4,0x7,2, 4,0x8,3, 4,0xB,4, 4,0xC,5, 4,0xE,6, 4,0xF,7, 5,0x13,8, 5,0x14,9, 5,0x7,10, 5,0x8,11, 6,0x8,12, 6,0x3,13, " +
            "6,0x34,14, 6,0x35,15, 6,0x2A,16, 6,0x2B,17, 7,0x27,18, 7,0xC,19, 7,0x8,20, 7,0x17,21, 7,0x3,22, 7,0x4,23, 7,0x28,24, 7,0x2B,25, 7,0x13,26, 7,0x24,27, " +
            "7,0x18,28, 8,0x2,29, 8,0x3,30, 8,0x1A,31, 8,0x1B,32, 8,0x12,33, 8,0x13,34, 8,0x14,35, 8,0x15,36, 8,0x16,37, 8,0x17,38, 8,0x28,39, 8,0x29,40, " +
            "8,0x2A,41, 8,0x2B,42, 8,0x2C,43, 8,0x2D,44, 8,0x4,45, 8,0x5,46, 8,0xA,47, 8,0xB,48, 8,0x52,49, 8,0x53,50, 8,0x54,51, 8,0x55,52, 8,0x24,53, 8,0x25,54, " +
            "8,0x58,55, 8,0x59,56, 8,0x5A,57, 8,0x5B,58, 8,0x4A,59, 8,0x4B,60, 8,0x32,61, 8,0x33,62, 8,0x34,63, 5,0x1B,64, 5,0x12,128, 6,0x17,192, 7,0x37,256, " +
            "8,0x36,320, 8,0x37,384, 8,0x64,448, 8,0x65,512, 8,0x68,576, 8,0x67,640, 9,0xCC,704, 9,0xCD,768, 9,0xD2,832, 9,0xD3,896, 9,0xD4,960, 9,0xD5,1024, " +
            "9,0xD6,1088, 9,0xD7,1152, 9,0xD8,1216, 9,0xD9,1280, 9,0xDA,1344, 9,0xDB,1408, 9,0x98,1472, 9,0x99,1536, 9,0x9A,1600, 6,0x18,1664, 9,0x9B,1728, " +
            "11,0x8,1792, 11,0xC,1856, 11,0xD,1920, 12,0x12,1984, 12,0x13,2048, 12,0x14,2112, 12,0x15,2176, 12,0x16,2240, 12,0x17,2304, 12,0x1C,2368, 12,0x1D,2432, " +
            "12,0x1E,2496, 12,0x1F,2560";
        const string CcBlackSrc =
            "12,0x1,-2, 10,0x37,0, 3,0x2,1, 2,0x3,2, 2,0x2,3, 3,0x3,4, 4,0x3,5, 4,0x2,6, 5,0x3,7, 6,0x5,8, 6,0x4,9, 7,0x4,10, 7,0x5,11, 7,0x7,12, 8,0x4,13, " +
            "8,0x7,14, 9,0x18,15, 10,0x17,16, 10,0x18,17, 10,0x8,18, 11,0x67,19, 11,0x68,20, 11,0x6C,21, 11,0x37,22, 11,0x28,23, 11,0x17,24, 11,0x18,25, " +
            "12,0xCA,26, 12,0xCB,27, 12,0xCC,28, 12,0xCD,29, 12,0x68,30, 12,0x69,31, 12,0x6A,32, 12,0x6B,33, 12,0xD2,34, 12,0xD3,35, 12,0xD4,36, 12,0xD5,37, " +
            "12,0xD6,38, 12,0xD7,39, 12,0x6C,40, 12,0x6D,41, 12,0xDA,42, 12,0xDB,43, 12,0x54,44, 12,0x55,45, 12,0x56,46, 12,0x57,47, 12,0x64,48, 12,0x65,49, " +
            "12,0x52,50, 12,0x53,51, 12,0x24,52, 12,0x37,53, 12,0x38,54, 12,0x27,55, 12,0x28,56, 12,0x58,57, 12,0x59,58, 12,0x2B,59, 12,0x2C,60, 12,0x5A,61, " +
            "12,0x66,62, 12,0x67,63, 10,0xF,64, 12,0xC8,128, 12,0xC9,192, 12,0x5B,256, 12,0x33,320, 12,0x34,384, 12,0x35,448, 13,0x6C,512, 13,0x6D,576, " +
            "13,0x4A,640, 13,0x4B,704, 13,0x4C,768, 13,0x4D,832, 13,0x72,896, 13,0x73,960, 13,0x74,1024, 13,0x75,1088, 13,0x76,1152, 13,0x77,1216, 13,0x52,1280, " +
            "13,0x53,1344, 13,0x54,1408, 13,0x55,1472, 13,0x5A,1536, 13,0x5B,1600, 13,0x64,1664, 13,0x65,1728, 11,0x8,1792, 11,0xC,1856, 11,0xD,1920, 12,0x12,1984, " +
            "12,0x13,2048, 12,0x14,2112, 12,0x15,2176, 12,0x16,2240, 12,0x17,2304, 12,0x1C,2368, 12,0x1D,2432, 12,0x1E,2496, 12,0x1F,2560";
        const string GlyphSrc =
            "A:41 AE:c6 AEacute:1fc AEmacron:1e2 Aacute:c1 Abreve:102 Abreveacute:1eae Abrevecyrillic:4d0 Abrevedotbelow:1eb6 Abrevegrave:1eb0 Abrevehookabove:1eb2 " +
            "Abrevetilde:1eb4 Acaron:1cd Acircumflex:c2 Acircumflexacute:1ea4 Acircumflexdotbelow:1eac Acircumflexgrave:1ea6 Acircumflexhookabove:1ea8 " +
            "Acircumflextilde:1eaa Acyrillic:410 Adblgrave:200 Adieresis:c4 Adieresiscyrillic:4d2 Adieresismacron:1de Adotbelow:1ea0 Adotmacron:1e0 Agrave:c0 " +
            "Ahookabove:1ea2 Aiecyrillic:4d4 Ainvertedbreve:202 Alpha:391 Alphatonos:386 Amacron:100 Aogonek:104 Aring:c5 Aringacute:1fa Aringbelow:1e00 Atilde:c3 " +
            "Aybarmenian:531 B:42 Bdotaccent:1e02 Bdotbelow:1e04 Becyrillic:411 Benarmenian:532 Beta:392 Bhook:181 Blinebelow:1e06 Btopbar:182 C:43 Caarmenian:53e " +
            "Cacute:106 Ccaron:10c Ccedilla:c7 Ccedillaacute:1e08 Ccircumflex:108 Cdot:10a Cdotaccent:10a Chaarmenian:549 Cheabkhasiancyrillic:4bc Checyrillic:427 " +
            "Chedescenderabkhasiancyrillic:4be Chedescendercyrillic:4b6 Chedieresiscyrillic:4f4 Cheharmenian:543 Chekhakassiancyrillic:4cb " +
            "Cheverticalstrokecyrillic:4b8 Chi:3a7 Chook:187 Coarmenian:551 D:44 DZ:1f1 DZcaron:1c4 Daarmenian:534 Dafrican:189 Dcaron:10e Dcedilla:1e10 " +
            "Dcircumflexbelow:1e12 Dcroat:110 Ddotaccent:1e0a Ddotbelow:1e0c Decyrillic:414 Deicoptic:3ee Delta:2206 Deltagreek:394 Dhook:18a Digammagreek:3dc " +
            "Djecyrillic:402 Dlinebelow:1e0e Dslash:110 Dtopbar:18b Dz:1f2 Dzcaron:1c5 Dzeabkhasiancyrillic:4e0 Dzecyrillic:405 Dzhecyrillic:40f E:45 Eacute:c9 " +
            "Ebreve:114 Ecaron:11a Ecedillabreve:1e1c Echarmenian:535 Ecircumflex:ca Ecircumflexacute:1ebe Ecircumflexbelow:1e18 Ecircumflexdotbelow:1ec6 " +
            "Ecircumflexgrave:1ec0 Ecircumflexhookabove:1ec2 Ecircumflextilde:1ec4 Ecyrillic:404 Edblgrave:204 Edieresis:cb Edot:116 Edotaccent:116 Edotbelow:1eb8 " +
            "Efcyrillic:424 Egrave:c8 Eharmenian:537 Ehookabove:1eba Eightroman:2167 Einvertedbreve:206 Eiotifiedcyrillic:464 Elcyrillic:41b Elevenroman:216a " +
            "Emacron:112 Emacronacute:1e16 Emacrongrave:1e14 Emcyrillic:41c Encyrillic:41d Endescendercyrillic:4a2 Eng:14a Enghecyrillic:4a4 Enhookcyrillic:4c7 " +
            "Eogonek:118 Eopen:190 Epsilon:395 Epsilontonos:388 Ercyrillic:420 Ereversed:18e Ereversedcyrillic:42d Escyrillic:421 Esdescendercyrillic:4aa Esh:1a9 " +
            "Eta:397 Etarmenian:538 Etatonos:389 Eth:d0 Etilde:1ebc Etildebelow:1e1a Euro:20ac Ezh:1b7 Ezhcaron:1ee Ezhreversed:1b8 F:46 Fdotaccent:1e1e " +
            "Feharmenian:556 Feicoptic:3e4 Fhook:191 Fitacyrillic:472 Fiveroman:2164 Fourroman:2163 G:47 Gacute:1f4 Gamma:393 Gammaafrican:194 Gangiacoptic:3ea " +
            "Gbreve:11e Gcaron:1e6 Gcedilla:122 Gcircumflex:11c Gcommaaccent:122 Gdot:120 Gdotaccent:120 Gecyrillic:413 Ghadarmenian:542 Ghemiddlehookcyrillic:494 " +
            "Ghestrokecyrillic:492 Gheupturncyrillic:490 Ghook:193 Gimarmenian:533 Gjecyrillic:403 Gmacron:1e20 Gsmallhook:29b Gstroke:1e4 H:48 H18533:25cf " +
            "H18543:25aa H18551:25ab H22073:25a1 Haabkhasiancyrillic:4a8 Hadescendercyrillic:4b2 Hardsigncyrillic:42a Hbar:126 Hbrevebelow:1e2a Hcedilla:1e28 " +
            "Hcircumflex:124 Hdieresis:1e26 Hdotaccent:1e22 Hdotbelow:1e24 Hoarmenian:540 Horicoptic:3e8 I:49 IAcyrillic:42f IJ:132 IUcyrillic:42e Iacute:cd " +
            "Ibreve:12c Icaron:1cf Icircumflex:ce Icyrillic:406 Idblgrave:208 Idieresis:cf Idieresisacute:1e2e Idieresiscyrillic:4e4 Idot:130 Idotaccent:130 " +
            "Idotbelow:1eca Iebrevecyrillic:4d6 Iecyrillic:415 Ifraktur:2111 Igrave:cc Ihookabove:1ec8 Iicyrillic:418 Iinvertedbreve:20a Iishortcyrillic:419 " +
            "Imacron:12a Imacroncyrillic:4e2 Iniarmenian:53b Iocyrillic:401 Iogonek:12e Iota:399 Iotaafrican:196 Iotadieresis:3aa Iotatonos:38a Istroke:197 " +
            "Itilde:128 Itildebelow:1e2c Izhitsacyrillic:474 Izhitsadblgravecyrillic:476 J:4a Jaarmenian:541 Jcircumflex:134 Jecyrillic:408 Jheharmenian:54b K:4b " +
            "Kabashkircyrillic:4a0 Kacute:1e30 Kacyrillic:41a Kadescendercyrillic:49a Kahookcyrillic:4c3 Kappa:39a Kastrokecyrillic:49e " +
            "Kaverticalstrokecyrillic:49c Kcaron:1e8 Kcedilla:136 Kcommaaccent:136 Kdotbelow:1e32 Keharmenian:554 Kenarmenian:53f Khacyrillic:425 Kheicoptic:3e6 " +
            "Khook:198 Kjecyrillic:40c Klinebelow:1e34 Koppacyrillic:480 Koppagreek:3de Ksicyrillic:46e L:4c LJ:1c7 Lacute:139 Lambda:39b Lcaron:13d Lcedilla:13b " +
            "Lcircumflexbelow:1e3c Lcommaaccent:13b Ldot:13f Ldotaccent:13f Ldotbelow:1e36 Ldotbelowmacron:1e38 Liwnarmenian:53c Lj:1c8 Ljecyrillic:409 " +
            "Llinebelow:1e3a Lslash:141 M:4d Macute:1e3e Mdotaccent:1e40 Mdotbelow:1e42 Menarmenian:544 Mturned:19c Mu:39c N:4e NJ:1ca Nacute:143 Ncaron:147 " +
            "Ncedilla:145 Ncircumflexbelow:1e4a Ncommaaccent:145 Ndotaccent:1e44 Ndotbelow:1e46 Nhookleft:19d Nineroman:2168 Nj:1cb Njecyrillic:40a Nlinebelow:1e48 " +
            "Nowarmenian:546 Ntilde:d1 Nu:39d O:4f OE:152 Oacute:d3 Obarredcyrillic:4e8 Obarreddieresiscyrillic:4ea Obreve:14e Ocaron:1d1 Ocenteredtilde:19f " +
            "Ocircumflex:d4 Ocircumflexacute:1ed0 Ocircumflexdotbelow:1ed8 Ocircumflexgrave:1ed2 Ocircumflexhookabove:1ed4 Ocircumflextilde:1ed6 Ocyrillic:41e " +
            "Odblacute:150 Odblgrave:20c Odieresis:d6 Odieresiscyrillic:4e6 Odotbelow:1ecc Ograve:d2 Oharmenian:555 Ohm:2126 Ohookabove:1ece Ohorn:1a0 " +
            "Ohornacute:1eda Ohorndotbelow:1ee2 Ohorngrave:1edc Ohornhookabove:1ede Ohorntilde:1ee0 Ohungarumlaut:150 Oi:1a2 Oinvertedbreve:20e Omacron:14c " +
            "Omacronacute:1e52 Omacrongrave:1e50 Omega:2126 Omegacyrillic:460 Omegagreek:3a9 Omegaroundcyrillic:47a Omegatitlocyrillic:47c Omegatonos:38f " +
            "Omicron:39f Omicrontonos:38c Oneroman:2160 Oogonek:1ea Oogonekmacron:1ec Oopen:186 Oslash:d8 Oslashacute:1fe Ostrokeacute:1fe Otcyrillic:47e Otilde:d5 " +
            "Otildeacute:1e4c Otildedieresis:1e4e P:50 Pacute:1e54 Pdotaccent:1e56 Pecyrillic:41f Peharmenian:54a Pemiddlehookcyrillic:4a6 Phi:3a6 Phook:1a4 Pi:3a0 " +
            "Piwrarmenian:553 Psi:3a8 Psicyrillic:470 Q:51 R:52 Raarmenian:54c Racute:154 Rcaron:158 Rcedilla:156 Rcommaaccent:156 Rdblgrave:210 Rdotaccent:1e58 " +
            "Rdotbelow:1e5a Rdotbelowmacron:1e5c Reharmenian:550 Rfraktur:211c Rho:3a1 Rinvertedbreve:212 Rlinebelow:1e5e Rsmallinverted:281 " +
            "Rsmallinvertedsuperior:2b6 S:53 SF010000:250c SF020000:2514 SF030000:2510 SF040000:2518 SF050000:253c SF060000:252c SF070000:2534 SF080000:251c " +
            "SF090000:2524 SF100000:2500 SF110000:2502 SF190000:2561 SF200000:2562 SF210000:2556 SF220000:2555 SF230000:2563 SF240000:2551 SF250000:2557 " +
            "SF260000:255d SF270000:255c SF280000:255b SF360000:255e SF370000:255f SF380000:255a SF390000:2554 SF400000:2569 SF410000:2566 SF420000:2560 " +
            "SF430000:2550 SF440000:256c SF450000:2567 SF460000:2568 SF470000:2564 SF480000:2565 SF490000:2559 SF500000:2558 SF510000:2552 SF520000:2553 " +
            "SF530000:256b SF540000:256a Sacute:15a Sacutedotaccent:1e64 Sampigreek:3e0 Scaron:160 Scarondotaccent:1e66 Scedilla:15e Schwa:18f Schwacyrillic:4d8 " +
            "Schwadieresiscyrillic:4da Scircumflex:15c Scommaaccent:218 Sdotaccent:1e60 Sdotbelow:1e62 Sdotbelowdotaccent:1e68 Seharmenian:54d Sevenroman:2166 " +
            "Shaarmenian:547 Shacyrillic:428 Shchacyrillic:429 Sheicoptic:3e2 Shhacyrillic:4ba Shimacoptic:3ec Sigma:3a3 Sixroman:2165 Softsigncyrillic:42c " +
            "Stigmagreek:3da T:54 Tau:3a4 Tbar:166 Tcaron:164 Tcedilla:162 Tcircumflexbelow:1e70 Tcommaaccent:162 Tdotaccent:1e6a Tdotbelow:1e6c Tecyrillic:422 " +
            "Tedescendercyrillic:4ac Tenroman:2169 Tetsecyrillic:4b4 Theta:398 Thook:1ac Thorn:de Threeroman:2162 Tiwnarmenian:54f Tlinebelow:1e6e Toarmenian:539 " +
            "Tonefive:1bc Tonesix:184 Tonetwo:1a7 Tretroflexhook:1ae Tsecyrillic:426 Tshecyrillic:40b Twelveroman:216b Tworoman:2161 U:55 Uacute:da Ubreve:16c " +
            "Ucaron:1d3 Ucircumflex:db Ucircumflexbelow:1e76 Ucyrillic:423 Udblacute:170 Udblgrave:214 Udieresis:dc Udieresisacute:1d7 Udieresisbelow:1e72 " +
            "Udieresiscaron:1d9 Udieresiscyrillic:4f0 Udieresisgrave:1db Udieresismacron:1d5 Udotbelow:1ee4 Ugrave:d9 Uhookabove:1ee6 Uhorn:1af Uhornacute:1ee8 " +
            "Uhorndotbelow:1ef0 Uhorngrave:1eea Uhornhookabove:1eec Uhorntilde:1eee Uhungarumlaut:170 Uhungarumlautcyrillic:4f2 Uinvertedbreve:216 Ukcyrillic:478 " +
            "Umacron:16a Umacroncyrillic:4ee Umacrondieresis:1e7a Uogonek:172 Upsilon:3a5 Upsilon1:3d2 Upsilonacutehooksymbolgreek:3d3 Upsilonafrican:1b1 " +
            "Upsilondieresis:3ab Upsilondieresishooksymbolgreek:3d4 Upsilonhooksymbol:3d2 Upsilontonos:38e Uring:16e Ushortcyrillic:40e Ustraightcyrillic:4ae " +
            "Ustraightstrokecyrillic:4b0 Utilde:168 Utildeacute:1e78 Utildebelow:1e74 V:56 Vdotbelow:1e7e Vecyrillic:412 Vewarmenian:54e Vhook:1b2 Voarmenian:548 " +
            "Vtilde:1e7c W:57 Wacute:1e82 Wcircumflex:174 Wdieresis:1e84 Wdotaccent:1e86 Wdotbelow:1e88 Wgrave:1e80 X:58 Xdieresis:1e8c Xdotaccent:1e8a " +
            "Xeharmenian:53d Xi:39e Y:59 Yacute:dd Yatcyrillic:462 Ycircumflex:176 Ydieresis:178 Ydotaccent:1e8e Ydotbelow:1ef4 Yericyrillic:42b " +
            "Yerudieresiscyrillic:4f8 Ygrave:1ef2 Yhook:1b3 Yhookabove:1ef6 Yiarmenian:545 Yicyrillic:407 Yiwnarmenian:552 Ytilde:1ef8 Yusbigcyrillic:46a " +
            "Yusbigiotifiedcyrillic:46c Yuslittlecyrillic:466 Yuslittleiotifiedcyrillic:468 Z:5a Zaarmenian:536 Zacute:179 Zcaron:17d Zcircumflex:1e90 Zdot:17b " +
            "Zdotaccent:17b Zdotbelow:1e92 Zecyrillic:417 Zedescendercyrillic:498 Zedieresiscyrillic:4de Zeta:396 Zhearmenian:53a Zhebrevecyrillic:4c1 " +
            "Zhecyrillic:416 Zhedescendercyrillic:496 Zhedieresiscyrillic:4dc Zlinebelow:1e94 Zstroke:1b5 a:61 aabengali:986 aacute:e1 aadeva:906 aagujarati:a86 " +
            "aagurmukhi:a06 aamatragurmukhi:a3e aavowelsignbengali:9be aavowelsigndeva:93e aavowelsigngujarati:abe abbreviationmarkarmenian:55f " +
            "abbreviationsigndeva:970 abengali:985 abreve:103 abreveacute:1eaf abrevecyrillic:4d1 abrevedotbelow:1eb7 abrevegrave:1eb1 abrevehookabove:1eb3 " +
            "abrevetilde:1eb5 acaron:1ce acircumflex:e2 acircumflexacute:1ea5 acircumflexdotbelow:1ead acircumflexgrave:1ea7 acircumflexhookabove:1ea9 " +
            "acircumflextilde:1eab acute:b4 acutebelowcmb:317 acutecmb:301 acutecomb:301 acutedeva:954 acutelowmod:2cf acutetonecmb:341 acyrillic:430 adblgrave:201 " +
            "addakgurmukhi:a71 adeva:905 adieresis:e4 adieresiscyrillic:4d3 adieresismacron:1df adotbelow:1ea1 adotmacron:1e1 ae:e6 aeacute:1fd aemacron:1e3 " +
            "afii00208:2015 afii08941:20a4 afii10017:410 afii10018:411 afii10019:412 afii10020:413 afii10021:414 afii10022:415 afii10023:401 afii10024:416 " +
            "afii10025:417 afii10026:418 afii10027:419 afii10028:41a afii10029:41b afii10030:41c afii10031:41d afii10032:41e afii10033:41f afii10034:420 " +
            "afii10035:421 afii10036:422 afii10037:423 afii10038:424 afii10039:425 afii10040:426 afii10041:427 afii10042:428 afii10043:429 afii10044:42a " +
            "afii10045:42b afii10046:42c afii10047:42d afii10048:42e afii10049:42f afii10050:490 afii10051:402 afii10052:403 afii10053:404 afii10054:405 " +
            "afii10055:406 afii10056:407 afii10057:408 afii10058:409 afii10059:40a afii10060:40b afii10061:40c afii10062:40e afii10065:430 afii10066:431 " +
            "afii10067:432 afii10068:433 afii10069:434 afii10070:435 afii10071:451 afii10072:436 afii10073:437 afii10074:438 afii10075:439 afii10076:43a " +
            "afii10077:43b afii10078:43c afii10079:43d afii10080:43e afii10081:43f afii10082:440 afii10083:441 afii10084:442 afii10085:443 afii10086:444 " +
            "afii10087:445 afii10088:446 afii10089:447 afii10090:448 afii10091:449 afii10092:44a afii10093:44b afii10094:44c afii10095:44d afii10096:44e " +
            "afii10097:44f afii10098:491 afii10099:452 afii10100:453 afii10101:454 afii10102:455 afii10103:456 afii10104:457 afii10105:458 afii10106:459 " +
            "afii10107:45a afii10108:45b afii10109:45c afii10110:45e afii10145:40f afii10146:462 afii10147:472 afii10148:474 afii10193:45f afii10194:463 " +
            "afii10195:473 afii10196:475 afii10846:4d9 afii299:200e afii300:200f afii301:200d afii57381:66a afii57388:60c afii57392:660 afii57393:661 afii57394:662 " +
            "afii57395:663 afii57396:664 afii57397:665 afii57398:666 afii57399:667 afii57400:668 afii57401:669 afii57403:61b afii57407:61f afii57409:621 " +
            "afii57410:622 afii57411:623 afii57412:624 afii57413:625 afii57414:626 afii57415:627 afii57416:628 afii57417:629 afii57418:62a afii57419:62b " +
            "afii57420:62c afii57421:62d afii57422:62e afii57423:62f afii57424:630 afii57425:631 afii57426:632 afii57427:633 afii57428:634 afii57429:635 " +
            "afii57430:636 afii57431:637 afii57432:638 afii57433:639 afii57434:63a afii57440:640 afii57441:641 afii57442:642 afii57443:643 afii57444:644 " +
            "afii57445:645 afii57446:646 afii57448:648 afii57449:649 afii57450:64a afii57451:64b afii57452:64c afii57453:64d afii57454:64e afii57455:64f " +
            "afii57456:650 afii57457:651 afii57458:652 afii57470:647 afii57505:6a4 afii57506:67e afii57507:686 afii57508:698 afii57509:6af afii57511:679 " +
            "afii57512:688 afii57513:691 afii57514:6ba afii57519:6d2 afii57534:6d5 afii57636:20aa afii57645:5be afii57658:5c3 afii57664:5d0 afii57665:5d1 " +
            "afii57666:5d2 afii57667:5d3 afii57668:5d4 afii57669:5d5 afii57670:5d6 afii57671:5d7 afii57672:5d8 afii57673:5d9 afii57674:5da afii57675:5db " +
            "afii57676:5dc afii57677:5dd afii57678:5de afii57679:5df afii57680:5e0 afii57681:5e1 afii57682:5e2 afii57683:5e3 afii57684:5e4 afii57685:5e5 " +
            "afii57686:5e6 afii57687:5e7 afii57688:5e8 afii57689:5e9 afii57690:5ea afii57716:5f0 afii57717:5f1 afii57718:5f2 afii57793:5b4 afii57794:5b5 " +
            "afii57795:5b6 afii57796:5bb afii57797:5b8 afii57798:5b7 afii57799:5b0 afii57800:5b2 afii57801:5b1 afii57802:5b3 afii57803:5c2 afii57804:5c1 " +
            "afii57806:5b9 afii57807:5bc afii57839:5bd afii57841:5bf afii57842:5c0 afii57929:2bc afii61248:2105 afii61289:2113 afii61352:2116 afii61573:202c " +
            "afii61574:202d afii61575:202e afii61664:200c afii63167:66d afii64937:2bd agrave:e0 agujarati:a85 agurmukhi:a05 ahookabove:1ea3 aibengali:990 " +
            "aideva:910 aiecyrillic:4d5 aigujarati:a90 aigurmukhi:a10 aimatragurmukhi:a48 ainarabic:639 ainvertedbreve:203 aivowelsignbengali:9c8 " +
            "aivowelsigndeva:948 aivowelsigngujarati:ac8 alef:5d0 alefarabic:627 alefhamzaabovearabic:623 alefhamzabelowarabic:625 alefhebrew:5d0 " +
            "alefmaddaabovearabic:622 alefmaksuraarabic:649 aleph:2135 allequal:224c alpha:3b1 alphatonos:3ac amacron:101 ampersand:26 angkhankhuthai:e5a " +
            "angle:2220 angleleft:2329 angleright:232a angstrom:212b anoteleia:387 anudattadeva:952 anusvarabengali:982 anusvaradeva:902 anusvaragujarati:a82 " +
            "aogonek:105 apostrophearmenian:55a apostrophemod:2bc approaches:2250 approxequal:2248 approxequalorimage:2252 approximatelyequal:2245 arc:2312 " +
            "arighthalfring:1e9a aring:e5 aringacute:1fb aringbelow:1e01 arrowboth:2194 arrowdashdown:21e3 arrowdashleft:21e0 arrowdashright:21e2 arrowdashup:21e1 " +
            "arrowdblboth:21d4 arrowdbldown:21d3 arrowdblleft:21d0 arrowdblright:21d2 arrowdblup:21d1 arrowdown:2193 arrowdownleft:2199 arrowdownright:2198 " +
            "arrowdownwhite:21e9 arrowheaddownmod:2c5 arrowheadleftmod:2c2 arrowheadrightmod:2c3 arrowheadupmod:2c4 arrowleft:2190 arrowleftdbl:21d0 " +
            "arrowleftdblstroke:21cd arrowleftoverright:21c6 arrowleftwhite:21e6 arrowright:2192 arrowrightdblstroke:21cf arrowrightoverleft:21c4 " +
            "arrowrightwhite:21e8 arrowtableft:21e4 arrowtabright:21e5 arrowup:2191 arrowupdn:2195 arrowupdnbse:21a8 arrowupdownbase:21a8 arrowupleft:2196 " +
            "arrowupleftofdown:21c5 arrowupright:2197 arrowupwhite:21e7 asciicircum:5e asciitilde:7e ascript:251 ascriptturned:252 asterisk:2a " +
            "asteriskaltonearabic:66d asteriskarabic:66d asteriskmath:2217 asterism:2042 asymptoticallyequal:2243 at:40 atilde:e3 aturned:250 aubengali:994 " +
            "audeva:914 augujarati:a94 augurmukhi:a14 aulengthmarkbengali:9d7 aumatragurmukhi:a4c auvowelsignbengali:9cc auvowelsigndeva:94c " +
            "auvowelsigngujarati:acc avagrahadeva:93d aybarmenian:561 ayin:5e2 ayinhebrew:5e2 b:62 babengali:9ac backslash:5c badeva:92c bagujarati:aac " +
            "bagurmukhi:a2c bahtthai:e3f bar:7c bdotaccent:1e03 bdotbelow:1e05 because:2235 becyrillic:431 beharabic:628 benarmenian:562 bet:5d1 beta:3b2 " +
            "betasymbolgreek:3d0 bethebrew:5d1 bhabengali:9ad bhadeva:92d bhagujarati:aad bhagurmukhi:a2d bhook:253 bilabialclick:298 bindigurmukhi:a02 " +
            "blackcircle:25cf blackdiamond:25c6 blackdownpointingtriangle:25bc blackleftpointingpointer:25c4 blackleftpointingtriangle:25c0 " +
            "blacklowerlefttriangle:25e3 blacklowerrighttriangle:25e2 blackrectangle:25ac blackrightpointingpointer:25ba blackrightpointingtriangle:25b6 " +
            "blacksmallsquare:25aa blacksquare:25a0 blackupperlefttriangle:25e4 blackupperrighttriangle:25e5 blackuppointingsmalltriangle:25b4 " +
            "blackuppointingtriangle:25b2 blinebelow:1e07 block:2588 bobaimaithai:e1a braceleft:7b braceright:7d bracketleft:5b bracketright:5d breve:2d8 " +
            "brevebelowcmb:32e brevecmb:306 breveinvertedbelowcmb:32f breveinvertedcmb:311 breveinverteddoublecmb:361 bridgebelowcmb:32a bridgeinvertedbelowcmb:33a " +
            "brokenbar:a6 bstroke:180 btopbar:183 bullet:2022 bulletinverse:25d8 bulletoperator:2219 bullseye:25ce c:63 caarmenian:56e cabengali:99a cacute:107 " +
            "cadeva:91a cagujarati:a9a cagurmukhi:a1a candrabindubengali:981 candrabinducmb:310 candrabindudeva:901 candrabindugujarati:a81 capslock:21ea " +
            "careof:2105 caron:2c7 caronbelowcmb:32c caroncmb:30c carriagereturn:21b5 ccaron:10d ccedilla:e7 ccedillaacute:1e09 ccircumflex:109 ccurl:255 cdot:10b " +
            "cdotaccent:10b cedilla:b8 cedillacmb:327 cent:a2 centigrade:2103 chaarmenian:579 chabengali:99b chadeva:91b chagujarati:a9b chagurmukhi:a1b " +
            "cheabkhasiancyrillic:4bd checyrillic:447 chedescenderabkhasiancyrillic:4bf chedescendercyrillic:4b7 chedieresiscyrillic:4f5 cheharmenian:573 " +
            "chekhakassiancyrillic:4cc cheverticalstrokecyrillic:4b9 chi:3c7 chochangthai:e0a chochanthai:e08 chochingthai:e09 chochoethai:e0c chook:188 " +
            "circle:25cb circlecopyrt:a9 circlemultiply:2297 circleot:2299 circleplus:2295 circlewithlefthalfblack:25d0 circlewithrighthalfblack:25d1 " +
            "circumflex:2c6 circumflexbelowcmb:32d circumflexcmb:302 clear:2327 clickalveolar:1c2 clickdental:1c0 clicklateral:1c1 clickretroflex:1c3 " +
            "coarmenian:581 colon:3a colonmonetary:20a1 colonsign:20a1 colontriangularhalfmod:2d1 colontriangularmod:2d0 comma:2c commaabovecmb:313 " +
            "commaaboverightcmb:315 commaarabic:60c commaarmenian:55d commareversedabovecmb:314 commareversedmod:2bd commaturnedabovecmb:312 commaturnedmod:2bb " +
            "congruent:2245 contourintegral:222e control:2303 controlACK:6 controlBEL:7 controlBS:8 controlCAN:18 controlCR:d controlDC1:11 controlDC2:12 " +
            "controlDC3:13 controlDC4:14 controlDEL:7f controlDLE:10 controlEM:19 controlENQ:5 controlEOT:4 controlESC:1b controlETB:17 controlETX:3 controlFF:c " +
            "controlFS:1c controlGS:1d controlHT:9 controlLF:a controlNAK:15 controlNULL:0 controlRS:1e controlSI:f controlSO:e controlSOT:2 controlSTX:1 " +
            "controlSUB:1a controlSYN:16 controlUS:1f controlVT:b copyright:a9 cruzeiro:20a2 cstretched:297 curlyand:22cf curlyor:22ce currency:a4 d:64 " +
            "daarmenian:564 dabengali:9a6 dadarabic:636 dadeva:926 dagesh:5bc dageshhebrew:5bc dagger:2020 daggerdbl:2021 dagujarati:aa6 dagurmukhi:a26 " +
            "dalarabic:62f dalet:5d3 dalethebrew:5d3 dammaarabic:64f dammalowarabic:64f dammatanaltonearabic:64c dammatanarabic:64c danda:964 dargahebrew:5a7 " +
            "dargalefthebrew:5a7 dasiapneumatacyrilliccmb:485 dblarchinvertedbelowcmb:32b dblarrowleft:21d4 dblarrowright:21d2 dbldanda:965 dblgravecmb:30f " +
            "dblintegral:222c dbllowline:2017 dbllowlinecmb:333 dbloverlinecmb:33f dblprimemod:2ba dblverticalbar:2016 dblverticallineabovecmb:30e dcaron:10f " +
            "dcedilla:1e11 dcircumflexbelow:1e13 dcroat:111 ddabengali:9a1 ddadeva:921 ddagujarati:aa1 ddagurmukhi:a21 ddalarabic:688 dddhadeva:95c ddhabengali:9a2 " +
            "ddhadeva:922 ddhagujarati:aa2 ddhagurmukhi:a22 ddotaccent:1e0b ddotbelow:1e0d decimalseparatorarabic:66b decimalseparatorpersian:66b decyrillic:434 " +
            "degree:b0 dehihebrew:5ad deicoptic:3ef deleteleft:232b deleteright:2326 delta:3b4 deltaturned:18d denominatorminusonenumeratorbengali:9f8 dezh:2a4 " +
            "dhabengali:9a7 dhadeva:927 dhagujarati:aa7 dhagurmukhi:a27 dhook:257 dialytikatonos:385 dialytikatonoscmb:344 dieresis:a8 dieresisbelowcmb:324 " +
            "dieresiscmb:308 dieresistonos:385 divide:f7 divides:2223 divisionslash:2215 djecyrillic:452 dkshade:2593 dlinebelow:1e0f dmacron:111 dnblock:2584 " +
            "dochadathai:e0e dodekthai:e14 dollar:24 dong:20ab dotaccent:2d9 dotaccentcmb:307 dotbelowcmb:323 dotbelowcomb:323 dotlessi:131 dotlessjstrokehook:284 " +
            "dotmath:22c5 dottedcircle:25cc downtackbelowcmb:31e downtackmod:2d5 dtail:256 dtopbar:18c dz:1f3 dzaltone:2a3 dzcaron:1c6 dzcurl:2a5 " +
            "dzeabkhasiancyrillic:4e1 dzecyrillic:455 dzhecyrillic:45f e:65 eacute:e9 ebengali:98f ebreve:115 ecandradeva:90d ecandragujarati:a8d " +
            "ecandravowelsigndeva:945 ecandravowelsigngujarati:ac5 ecaron:11b ecedillabreve:1e1d echarmenian:565 echyiwnarmenian:587 ecircumflex:ea " +
            "ecircumflexacute:1ebf ecircumflexbelow:1e19 ecircumflexdotbelow:1ec7 ecircumflexgrave:1ec1 ecircumflexhookabove:1ec3 ecircumflextilde:1ec5 " +
            "ecyrillic:454 edblgrave:205 edeva:90f edieresis:eb edot:117 edotaccent:117 edotbelow:1eb9 eegurmukhi:a0f eematragurmukhi:a47 efcyrillic:444 egrave:e8 " +
            "egujarati:a8f eharmenian:567 ehookabove:1ebb eight:38 eightarabic:668 eightbengali:9ee eightdeva:96e eightgujarati:aee eightgurmukhi:a6e " +
            "eighthackarabic:668 eightinferior:2088 eightpersian:6f8 eightroman:2177 eightsuperior:2078 eightthai:e58 einvertedbreve:207 eiotifiedcyrillic:465 " +
            "ekonkargurmukhi:a74 elcyrillic:43b element:2208 elevenroman:217a ellipsis:2026 ellipsisvertical:22ee emacron:113 emacronacute:1e17 emacrongrave:1e15 " +
            "emcyrillic:43c emdash:2014 emphasismarkarmenian:55b emptyset:2205 encyrillic:43d endash:2013 endescendercyrillic:4a3 eng:14b enghecyrillic:4a5 " +
            "enhookcyrillic:4c8 enspace:2002 eogonek:119 eopen:25b eopenclosed:29a eopenreversed:25c eopenreversedclosed:25e eopenreversedhook:25d epsilon:3b5 " +
            "epsilontonos:3ad equal:3d equalsuperior:207c equivalence:2261 ercyrillic:440 ereversed:258 ereversedcyrillic:44d escyrillic:441 " +
            "esdescendercyrillic:4ab esh:283 eshcurl:286 eshortdeva:90e eshortvowelsigndeva:946 eshreversedloop:1aa eshsquatreversed:285 estimated:212e eta:3b7 " +
            "etarmenian:568 etatonos:3ae eth:f0 etilde:1ebd etildebelow:1e1b etnahtafoukhhebrew:591 etnahtafoukhlefthebrew:591 etnahtahebrew:591 " +
            "etnahtalefthebrew:591 eturned:1dd euro:20ac evowelsignbengali:9c7 evowelsigndeva:947 evowelsigngujarati:ac7 exclam:21 exclamarmenian:55c " +
            "exclamdbl:203c exclamdown:a1 existential:2203 ezh:292 ezhcaron:1ef ezhcurl:293 ezhreversed:1b9 ezhtail:1ba f:66 fadeva:95e fagurmukhi:a5e " +
            "fahrenheit:2109 fathaarabic:64e fathalowarabic:64e fathatanarabic:64b fdotaccent:1e1f feharabic:641 feharmenian:586 feicoptic:3e5 ff:fb00 f_f:fb00 " +
            "ffi:fb03 f_f_i:fb03 ffl:fb04 f_f_l:fb04 fi:fb01 f_i:fb01 figuredash:2012 filledbox:25a0 filledrect:25ac finalkaf:5da finalkafhebrew:5da finalmem:5dd " +
            "finalmemhebrew:5dd finalnun:5df finalnunhebrew:5df finalpe:5e3 finalpehebrew:5e3 finaltsadi:5e5 finaltsadihebrew:5e5 firsttonechinese:2c9 fisheye:25c9 " +
            "fitacyrillic:473 five:35 fivearabic:665 fivebengali:9eb fivedeva:96b fiveeighths:215d fivegujarati:aeb fivegurmukhi:a6b fivehackarabic:665 " +
            "fiveinferior:2085 fivepersian:6f5 fiveroman:2174 fivesuperior:2075 fivethai:e55 fl:fb02 f_l:fb02 florin:192 fofanthai:e1f fofathai:e1d fongmanthai:e4f " +
            "forall:2200 four:34 fourarabic:664 fourbengali:9ea fourdeva:96a fourgujarati:aea fourgurmukhi:a6a fourhackarabic:664 fourinferior:2084 " +
            "fournumeratorbengali:9f7 fourpersian:6f4 fourroman:2173 foursuperior:2074 fourthai:e54 fourthtonechinese:2cb fraction:2044 franc:20a3 g:67 " +
            "gabengali:997 gacute:1f5 gadeva:917 gafarabic:6af gagujarati:a97 gagurmukhi:a17 gamma:3b3 gammalatinsmall:263 gammasuperior:2e0 gangiacoptic:3eb " +
            "gbreve:11f gcaron:1e7 gcedilla:123 gcircumflex:11d gcommaaccent:123 gdot:121 gdotaccent:121 gecyrillic:433 geometricallyequal:2251 " +
            "gereshaccenthebrew:59c gereshhebrew:5f3 gereshmuqdamhebrew:59d germandbls:df gershayimaccenthebrew:59e gershayimhebrew:5f4 ghabengali:998 " +
            "ghadarmenian:572 ghadeva:918 ghagujarati:a98 ghagurmukhi:a18 ghainarabic:63a ghemiddlehookcyrillic:495 ghestrokecyrillic:493 gheupturncyrillic:491 " +
            "ghhadeva:95a ghhagurmukhi:a5a ghook:260 gimarmenian:563 gimel:5d2 gimelhebrew:5d2 gjecyrillic:453 glottalinvertedstroke:1be glottalstop:294 " +
            "glottalstopinverted:296 glottalstopmod:2c0 glottalstopreversed:295 glottalstopreversedmod:2c1 glottalstopreversedsuperior:2e4 glottalstopstroke:2a1 " +
            "glottalstopstrokereversed:2a2 gmacron:1e21 gradient:2207 grave:60 gravebelowcmb:316 gravecmb:300 gravecomb:300 gravedeva:953 gravelowmod:2ce " +
            "gravetonecmb:340 greater:3e greaterequal:2265 greaterequalorless:22db greaterorequivalent:2273 greaterorless:2277 greateroverequal:2267 gscript:261 " +
            "gstroke:1e5 guillemotleft:ab guillemotright:bb guilsinglleft:2039 guilsinglright:203a h:68 haabkhasiancyrillic:4a9 haaltonearabic:6c1 habengali:9b9 " +
            "hadescendercyrillic:4b3 hadeva:939 hagujarati:ab9 hagurmukhi:a39 haharabic:62d halantgurmukhi:a4d hamzaarabic:621 hamzalowarabic:621 " +
            "hardsigncyrillic:44a harpoonleftbarbup:21bc harpoonrightbarbup:21c0 hatafpatah:5b2 hatafpatah16:5b2 hatafpatah23:5b2 hatafpatah2f:5b2 " +
            "hatafpatahhebrew:5b2 hatafpatahnarrowhebrew:5b2 hatafpatahquarterhebrew:5b2 hatafpatahwidehebrew:5b2 hatafqamats:5b3 hatafqamats1b:5b3 " +
            "hatafqamats28:5b3 hatafqamats34:5b3 hatafqamatshebrew:5b3 hatafqamatsnarrowhebrew:5b3 hatafqamatsquarterhebrew:5b3 hatafqamatswidehebrew:5b3 " +
            "hatafsegol:5b1 hatafsegol17:5b1 hatafsegol24:5b1 hatafsegol30:5b1 hatafsegolhebrew:5b1 hatafsegolnarrowhebrew:5b1 hatafsegolquarterhebrew:5b1 " +
            "hatafsegolwidehebrew:5b1 hbar:127 hbrevebelow:1e2b hcedilla:1e29 hcircumflex:125 hdieresis:1e27 hdotaccent:1e23 hdotbelow:1e25 he:5d4 " +
            "hehaltonearabic:6c1 heharabic:647 hehebrew:5d4 henghook:267 het:5d7 hethebrew:5d7 hhook:266 hhooksuperior:2b1 hiriq:5b4 hiriq14:5b4 hiriq21:5b4 " +
            "hiriq2d:5b4 hiriqhebrew:5b4 hiriqnarrowhebrew:5b4 hiriqquarterhebrew:5b4 hiriqwidehebrew:5b4 hlinebelow:1e96 hoarmenian:570 hohipthai:e2b holam:5b9 " +
            "holam19:5b9 holam26:5b9 holam32:5b9 holamhebrew:5b9 holamnarrowhebrew:5b9 holamquarterhebrew:5b9 holamwidehebrew:5b9 honokhukthai:e2e " +
            "hookabovecomb:309 hookcmb:309 hookpalatalizedbelowcmb:321 hookretroflexbelowcmb:322 horicoptic:3e9 horizontalbar:2015 horncmb:31b house:2302 " +
            "hsuperior:2b0 hturned:265 hungarumlaut:2dd hungarumlautcmb:30b hv:195 hyphen:2d hyphentwo:2010 i:69 iacute:ed iacyrillic:44f ibengali:987 ibreve:12d " +
            "icaron:1d0 icircumflex:ee icyrillic:456 idblgrave:209 ideva:907 idieresis:ef idieresisacute:1e2f idieresiscyrillic:4e5 idotbelow:1ecb " +
            "iebrevecyrillic:4d7 iecyrillic:435 igrave:ec igujarati:a87 igurmukhi:a07 ihookabove:1ec9 iibengali:988 iicyrillic:438 iideva:908 iigujarati:a88 " +
            "iigurmukhi:a08 iimatragurmukhi:a40 iinvertedbreve:20b iishortcyrillic:439 iivowelsignbengali:9c0 iivowelsigndeva:940 iivowelsigngujarati:ac0 ij:133 " +
            "ilde:2dc iluyhebrew:5ac imacron:12b imacroncyrillic:4e3 imageorapproximatelyequal:2253 imatragurmukhi:a3f increment:2206 infinity:221e iniarmenian:56b " +
            "integral:222b integralbottom:2321 integralbt:2321 integraltop:2320 integraltp:2320 intersection:2229 invbullet:25d8 invcircle:25d9 iocyrillic:451 " +
            "iogonek:12f iota:3b9 iotadieresis:3ca iotadieresistonos:390 iotalatin:269 iotatonos:3af irigurmukhi:a72 issharbengali:9fa istroke:268 itilde:129 " +
            "itildebelow:1e2d iucyrillic:44e ivowelsignbengali:9bf ivowelsigndeva:93f ivowelsigngujarati:abf izhitsacyrillic:475 izhitsadblgravecyrillic:477 j:6a " +
            "jaarmenian:571 jabengali:99c jadeva:91c jagujarati:a9c jagurmukhi:a1c jcaron:1f0 jcircumflex:135 jcrossedtail:29d jdotlessstroke:25f jecyrillic:458 " +
            "jeemarabic:62c jeharabic:698 jhabengali:99d jhadeva:91d jhagujarati:a9d jhagurmukhi:a1d jheharmenian:57b jsuperior:2b2 k:6b kabashkircyrillic:4a1 " +
            "kabengali:995 kacute:1e31 kacyrillic:43a kadescendercyrillic:49b kadeva:915 kaf:5db kafarabic:643 kafhebrew:5db kagujarati:a95 kagurmukhi:a15 " +
            "kahookcyrillic:4c4 kappa:3ba kappasymbolgreek:3f0 kashidaautoarabic:640 kashidaautonosidebearingarabic:640 kasraarabic:650 kasratanarabic:64d " +
            "kastrokecyrillic:49f kaverticalstrokecyrillic:49d kcaron:1e9 kcedilla:137 kcommaaccent:137 kdotbelow:1e33 keharmenian:584 kenarmenian:56f " +
            "kgreenlandic:138 khabengali:996 khacyrillic:445 khadeva:916 khagujarati:a96 khagurmukhi:a16 khaharabic:62e kheicoptic:3e7 khhadeva:959 " +
            "khhagurmukhi:a59 khokhaithai:e02 khokhonthai:e05 khokhuatthai:e03 khokhwaithai:e04 khomutthai:e5b khook:199 khorakhangthai:e06 kjecyrillic:45c " +
            "klinebelow:1e35 kokaithai:e01 koppacyrillic:481 koroniscmb:343 ksicyrillic:46f kturned:29e l:6c labengali:9b2 lacute:13a ladeva:932 lagujarati:ab2 " +
            "lagurmukhi:a32 lakkhangyaothai:e45 lamarabic:644 lambda:3bb lambdastroke:19b lamed:5dc lamedhebrew:5dc largecircle:25ef lbar:19a lbelt:26c lcaron:13e " +
            "lcedilla:13c lcircumflexbelow:1e3d lcommaaccent:13c ldot:140 ldotaccent:140 ldotbelow:1e37 ldotbelowmacron:1e39 leftangleabovecmb:31a " +
            "lefttackbelowcmb:318 less:3c lessequal:2264 lessequalorgreater:22da lessorequivalent:2272 lessorgreater:2276 lessoverequal:2266 lezh:26e lfblock:258c " +
            "lhookretroflex:26d lira:20a4 liwnarmenian:56c lj:1c9 ljecyrillic:459 lladeva:933 llagujarati:ab3 llinebelow:1e3b llladeva:934 llvocalicbengali:9e1 " +
            "llvocalicdeva:961 llvocalicvowelsignbengali:9e3 llvocalicvowelsigndeva:963 lmiddletilde:26b lochulathai:e2c logicaland:2227 logicalnot:ac " +
            "logicalnotreversed:2310 logicalor:2228 lolingthai:e25 longs:17f lowlinecmb:332 lozenge:25ca lslash:142 lsquare:2113 ltshade:2591 luthai:e26 " +
            "lvocalicbengali:98c lvocalicdeva:90c lvocalicvowelsignbengali:9e2 lvocalicvowelsigndeva:962 m:6d mabengali:9ae macron:af macronbelowcmb:331 " +
            "macroncmb:304 macronlowmod:2cd macute:1e3f madeva:92e magujarati:aae magurmukhi:a2e mahapakhhebrew:5a4 mahapakhlefthebrew:5a4 maichattawathai:e4b " +
            "maiekthai:e48 maihanakatthai:e31 maitaikhuthai:e47 maithothai:e49 maitrithai:e4a maiyamokthai:e46 maqafhebrew:5be masoracirclehebrew:5af " +
            "mdotaccent:1e41 mdotbelow:1e43 meemarabic:645 mem:5de memhebrew:5de menarmenian:574 merkhahebrew:5a5 merkhakefulahebrew:5a6 merkhakefulalefthebrew:5a6 " +
            "merkhalefthebrew:5a5 mhook:271 middot:b7 minus:2212 minusbelowcmb:320 minuscircle:2296 minusmod:2d7 minusplus:2213 minute:2032 mlonglegturned:270 " +
            "momathai:e21 mturned:26f mu:b5 mu1:b5 muchgreater:226b muchless:226a mugreek:3bc multiply:d7 munahhebrew:5a3 munahlefthebrew:5a3 n:6e nabengali:9a8 " +
            "nabla:2207 nacute:144 nadeva:928 nagujarati:aa8 nagurmukhi:a28 napostrophe:149 nbspace:a0 ncaron:148 ncedilla:146 ncircumflexbelow:1e4b " +
            "ncommaaccent:146 ndotaccent:1e45 ndotbelow:1e47 newsheqelsign:20aa ngabengali:999 ngadeva:919 ngagujarati:a99 ngagurmukhi:a19 ngonguthai:e07 " +
            "nhookleft:272 nhookretroflex:273 nikhahitthai:e4d nine:39 ninearabic:669 ninebengali:9ef ninedeva:96f ninegujarati:aef ninegurmukhi:a6f " +
            "ninehackarabic:669 nineinferior:2089 ninepersian:6f9 nineroman:2178 ninesuperior:2079 ninethai:e59 nj:1cc njecyrillic:45a nlegrightlong:19e " +
            "nlinebelow:1e49 nnabengali:9a3 nnadeva:923 nnagujarati:aa3 nnagurmukhi:a23 nnnadeva:929 nonbreakingspace:a0 nonenthai:e13 nonuthai:e19 noonarabic:646 " +
            "noonghunnaarabic:6ba notcontains:220c notelement:2209 notelementof:2209 notequal:2260 notgreater:226f notgreaternorequal:2271 notgreaternorless:2279 " +
            "notidentical:2262 notless:226e notlessnorequal:2270 notparallel:2226 notprecedes:2280 notsubset:2284 notsucceeds:2281 notsuperset:2285 nowarmenian:576 " +
            "nsuperior:207f ntilde:f1 nu:3bd nuktabengali:9bc nuktadeva:93c nuktagujarati:abc nuktagurmukhi:a3c numbersign:23 numeralsigngreek:374 " +
            "numeralsignlowergreek:375 numero:2116 nun:5e0 nunhebrew:5e0 nyabengali:99e nyadeva:91e nyagujarati:a9e nyagurmukhi:a1e o:6f oacute:f3 oangthai:e2d " +
            "obarred:275 obarredcyrillic:4e9 obarreddieresiscyrillic:4eb obengali:993 obreve:14f ocandradeva:911 ocandragujarati:a91 ocandravowelsigndeva:949 " +
            "ocandravowelsigngujarati:ac9 ocaron:1d2 ocircumflex:f4 ocircumflexacute:1ed1 ocircumflexdotbelow:1ed9 ocircumflexgrave:1ed3 ocircumflexhookabove:1ed5 " +
            "ocircumflextilde:1ed7 ocyrillic:43e odblacute:151 odblgrave:20d odeva:913 odieresis:f6 odieresiscyrillic:4e7 odotbelow:1ecd oe:153 ogonek:2db " +
            "ogonekcmb:328 ograve:f2 ogujarati:a93 oharmenian:585 ohookabove:1ecf ohorn:1a1 ohornacute:1edb ohorndotbelow:1ee3 ohorngrave:1edd ohornhookabove:1edf " +
            "ohorntilde:1ee1 ohungarumlaut:151 oi:1a3 oinvertedbreve:20f olehebrew:5ab omacron:14d omacronacute:1e53 omacrongrave:1e51 omdeva:950 omega:3c9 " +
            "omega1:3d6 omegacyrillic:461 omegalatinclosed:277 omegaroundcyrillic:47b omegatitlocyrillic:47d omegatonos:3ce omgujarati:ad0 omicron:3bf " +
            "omicrontonos:3cc one:31 onearabic:661 onebengali:9e7 onedeva:967 onedotenleader:2024 oneeighth:215b onegujarati:ae7 onegurmukhi:a67 onehackarabic:661 " +
            "onehalf:bd oneinferior:2081 onenumeratorbengali:9f4 onepersian:6f1 onequarter:bc oneroman:2170 onesuperior:b9 onethai:e51 onethird:2153 oogonek:1eb " +
            "oogonekmacron:1ed oogurmukhi:a13 oomatragurmukhi:a4b oopen:254 openbullet:25e6 option:2325 ordfeminine:aa ordmasculine:ba orthogonal:221f " +
            "oshortdeva:912 oshortvowelsigndeva:94a oslash:f8 oslashacute:1ff ostrokeacute:1ff otcyrillic:47f otilde:f5 otildeacute:1e4d otildedieresis:1e4f " +
            "overline:203e overlinecmb:305 overscore:af ovowelsignbengali:9cb ovowelsigndeva:94b ovowelsigngujarati:acb p:70 pabengali:9aa pacute:1e55 padeva:92a " +
            "pagedown:21df pageup:21de pagujarati:aaa pagurmukhi:a2a paiyannoithai:e2f palatalizationcyrilliccmb:484 palochkacyrillic:4c0 paragraph:b6 " +
            "parallel:2225 parenleft:28 parenleftinferior:208d parenleftsuperior:207d parenright:29 parenrightinferior:208e parenrightsuperior:207e " +
            "partialdiff:2202 paseqhebrew:5c0 pashtahebrew:599 patah:5b7 patah11:5b7 patah1d:5b7 patah2a:5b7 patahhebrew:5b7 patahnarrowhebrew:5b7 " +
            "patahquarterhebrew:5b7 patahwidehebrew:5b7 pazerhebrew:5a1 pdotaccent:1e57 pe:5e4 pecyrillic:43f peharabic:67e peharmenian:57a pehebrew:5e4 " +
            "pemiddlehookcyrillic:4a7 percent:25 percentarabic:66a period:2e periodarmenian:589 periodcentered:b7 perispomenigreekcmb:342 perpendicular:22a5 " +
            "perthousand:2030 peseta:20a7 phabengali:9ab phadeva:92b phagujarati:aab phagurmukhi:a2b phi:3c6 phi1:3d5 philatin:278 phinthuthai:e3a " +
            "phisymbolgreek:3d5 phook:1a5 phophanthai:e1e phophungthai:e1c phosamphaothai:e20 pi:3c0 pisymbolgreek:3d6 piwrarmenian:583 planckover2pi:210f " +
            "planckover2pi1:210f plus:2b plusbelowcmb:31f pluscircle:2295 plusminus:b1 plusmod:2d6 plussuperior:207a poplathai:e1b precedes:227a prescription:211e " +
            "primemod:2b9 primereversed:2035 product:220f projective:2305 propellor:2318 propersubset:2282 propersuperset:2283 proportion:2237 proportional:221d " +
            "psi:3c8 psicyrillic:471 psilipneumatacyrilliccmb:486 q:71 qadeva:958 qadmahebrew:5a8 qafarabic:642 qamats:5b8 qamats10:5b8 qamats1a:5b8 qamats1c:5b8 " +
            "qamats27:5b8 qamats29:5b8 qamats33:5b8 qamatsde:5b8 qamatshebrew:5b8 qamatsnarrowhebrew:5b8 qamatsqatanhebrew:5b8 qamatsqatannarrowhebrew:5b8 " +
            "qamatsqatanquarterhebrew:5b8 qamatsqatanwidehebrew:5b8 qamatsquarterhebrew:5b8 qamatswidehebrew:5b8 qarneyparahebrew:59f qhook:2a0 qof:5e7 " +
            "qofhebrew:5e7 qubuts:5bb qubuts18:5bb qubuts25:5bb qubuts31:5bb qubutshebrew:5bb qubutsnarrowhebrew:5bb qubutsquarterhebrew:5bb qubutswidehebrew:5bb " +
            "question:3f questionarabic:61f questionarmenian:55e questiondown:bf questiongreek:37e quotedbl:22 quotedblbase:201e quotedblleft:201c " +
            "quotedblright:201d quoteleft:2018 quoteleftreversed:201b quotereversed:201b quoteright:2019 quoterightn:149 quotesinglbase:201a quotesingle:27 r:72 " +
            "raarmenian:57c rabengali:9b0 racute:155 radeva:930 radical:221a rafe:5bf rafehebrew:5bf ragujarati:ab0 ragurmukhi:a30 ralowerdiagonalbengali:9f1 " +
            "ramiddlediagonalbengali:9f0 ramshorn:264 ratio:2236 rcaron:159 rcedilla:157 rcommaaccent:157 rdblgrave:211 rdotaccent:1e59 rdotbelow:1e5b " +
            "rdotbelowmacron:1e5d referencemark:203b reflexsubset:2286 reflexsuperset:2287 registered:ae reharabic:631 reharmenian:580 resh:5e8 reshhebrew:5e8 " +
            "reversedtilde:223d reviahebrew:597 reviamugrashhebrew:597 revlogicalnot:2310 rfishhook:27e rfishhookreversed:27f rhabengali:9dd rhadeva:95d rho:3c1 " +
            "rhook:27d rhookturned:27b rhookturnedsuperior:2b5 rhosymbolgreek:3f1 rhotichookmod:2de rightangle:221f righttackbelowcmb:319 righttriangle:22bf " +
            "ring:2da ringbelowcmb:325 ringcmb:30a ringhalfleft:2bf ringhalfleftarmenian:559 ringhalfleftbelowcmb:31c ringhalfleftcentered:2d3 ringhalfright:2be " +
            "ringhalfrightbelowcmb:339 ringhalfrightcentered:2d2 rinvertedbreve:213 rlinebelow:1e5f rlongleg:27c rlonglegturned:27a roruathai:e23 rrabengali:9dc " +
            "rradeva:931 rragurmukhi:a5c rreharabic:691 rrvocalicbengali:9e0 rrvocalicdeva:960 rrvocalicgujarati:ae0 rrvocalicvowelsignbengali:9c4 " +
            "rrvocalicvowelsigndeva:944 rrvocalicvowelsigngujarati:ac4 rtblock:2590 rturned:279 rturnedsuperior:2b4 rupeemarkbengali:9f2 rupeesignbengali:9f3 " +
            "ruthai:e24 rvocalicbengali:98b rvocalicdeva:90b rvocalicgujarati:a8b rvocalicvowelsignbengali:9c3 rvocalicvowelsigndeva:943 " +
            "rvocalicvowelsigngujarati:ac3 s:73 sabengali:9b8 sacute:15b sacutedotaccent:1e65 sadarabic:635 sadeva:938 sagujarati:ab8 sagurmukhi:a38 samekh:5e1 " +
            "samekhhebrew:5e1 saraaathai:e32 saraaethai:e41 saraaimaimalaithai:e44 saraaimaimuanthai:e43 saraamthai:e33 saraathai:e30 saraethai:e40 saraiithai:e35 " +
            "saraithai:e34 saraothai:e42 saraueethai:e37 sarauethai:e36 sarauthai:e38 sarauuthai:e39 scaron:161 scarondotaccent:1e67 scedilla:15f schwa:259 " +
            "schwacyrillic:4d9 schwadieresiscyrillic:4db schwahook:25a scircumflex:15d scommaaccent:219 sdotaccent:1e61 sdotbelow:1e63 sdotbelowdotaccent:1e69 " +
            "seagullbelowcmb:33c second:2033 secondtonechinese:2ca section:a7 seenarabic:633 segol:5b6 segol13:5b6 segol1f:5b6 segol2c:5b6 segolhebrew:5b6 " +
            "segolnarrowhebrew:5b6 segolquarterhebrew:5b6 segoltahebrew:592 segolwidehebrew:5b6 seharmenian:57d semicolon:3b semicolonarabic:61b seven:37 " +
            "sevenarabic:667 sevenbengali:9ed sevendeva:96d seveneighths:215e sevengujarati:aed sevengurmukhi:a6d sevenhackarabic:667 seveninferior:2087 " +
            "sevenpersian:6f7 sevenroman:2176 sevensuperior:2077 seventhai:e57 sfthyphen:ad shaarmenian:577 shabengali:9b6 shacyrillic:448 shaddaarabic:651 " +
            "shade:2592 shadedark:2593 shadelight:2591 shademedium:2592 shadeva:936 shagujarati:ab6 shagurmukhi:a36 shalshelethebrew:593 shchacyrillic:449 " +
            "sheenarabic:634 sheicoptic:3e3 sheqel:20aa sheqelhebrew:20aa sheva:5b0 sheva115:5b0 sheva15:5b0 sheva22:5b0 sheva2e:5b0 shevahebrew:5b0 " +
            "shevanarrowhebrew:5b0 shevaquarterhebrew:5b0 shevawidehebrew:5b0 shhacyrillic:4bb shimacoptic:3ed shin:5e9 shindothebrew:5c1 shinhebrew:5e9 shook:282 " +
            "sigma:3c3 sigma1:3c2 sigmafinal:3c2 sigmalunatesymbolgreek:3f2 siluqhebrew:5bd siluqlefthebrew:5bd similar:223c sindothebrew:5c2 six:36 sixarabic:666 " +
            "sixbengali:9ec sixdeva:96c sixgujarati:aec sixgurmukhi:a6c sixhackarabic:666 sixinferior:2086 sixpersian:6f6 sixroman:2175 sixsuperior:2076 " +
            "sixteencurrencydenominatorbengali:9f9 sixthai:e56 slash:2f slong:17f slongdotaccent:1e9b sofpasuqhebrew:5c3 softhyphen:ad softsigncyrillic:44c " +
            "soliduslongoverlaycmb:338 solidusshortoverlaycmb:337 sorusithai:e29 sosalathai:e28 sosothai:e0b sosuathai:e2a space:20 spacehackarabic:20 " +
            "squarebelowcmb:33b squarediagonalcrosshatchfill:25a9 squarehorizontalfill:25a4 squareorthogonalcrosshatchfill:25a6 " +
            "squareupperlefttolowerrightfill:25a7 squareupperrighttolowerleftfill:25a8 squareverticalfill:25a5 squarewhitewithsmallblack:25a3 ssabengali:9b7 " +
            "ssadeva:937 ssagujarati:ab7 sterling:a3 strokelongoverlaycmb:336 strokeshortoverlaycmb:335 subset:2282 subsetnotequal:228a subsetorequal:2286 " +
            "succeeds:227b suchthat:220b sukunarabic:652 summation:2211 superset:2283 supersetnotequal:228b supersetorequal:2287 t:74 tabengali:9a4 tackdown:22a4 " +
            "tackleft:22a3 tadeva:924 tagujarati:aa4 tagurmukhi:a24 taharabic:637 tatweelarabic:640 tau:3c4 tav:5ea tavhebrew:5ea tbar:167 tcaron:165 tccurl:2a8 " +
            "tcedilla:163 tcheharabic:686 tcircumflexbelow:1e71 tcommaaccent:163 tdieresis:1e97 tdotaccent:1e6b tdotbelow:1e6d tecyrillic:442 " +
            "tedescendercyrillic:4ad teharabic:62a tehmarbutaarabic:629 telephone:2121 telishagedolahebrew:5a0 telishaqetanahebrew:5a9 tenroman:2179 tesh:2a7 " +
            "tet:5d8 tethebrew:5d8 tetsecyrillic:4b5 tevirhebrew:59b tevirlefthebrew:59b thabengali:9a5 thadeva:925 thagujarati:aa5 thagurmukhi:a25 thalarabic:630 " +
            "thanthakhatthai:e4c theharabic:62b thereexists:2203 therefore:2234 theta:3b8 theta1:3d1 thetasymbolgreek:3d1 thonangmonthothai:e11 thook:1ad " +
            "thophuthaothai:e12 thorn:fe thothahanthai:e17 thothanthai:e10 thothongthai:e18 thothungthai:e16 thousandcyrillic:482 thousandsseparatorarabic:66c " +
            "thousandsseparatorpersian:66c three:33 threearabic:663 threebengali:9e9 threedeva:969 threeeighths:215c threegujarati:ae9 threegurmukhi:a69 " +
            "threehackarabic:663 threeinferior:2083 threenumeratorbengali:9f6 threepersian:6f3 threequarters:be threeroman:2172 threesuperior:b3 threethai:e53 " +
            "tilde:2dc tildebelowcmb:330 tildecmb:303 tildecomb:303 tildedoublecmb:360 tildeoperator:223c tildeoverlaycmb:334 tildeverticalcmb:33e timescircle:2297 " +
            "tipehahebrew:596 tipehalefthebrew:596 tippigurmukhi:a70 titlocyrilliccmb:483 tiwnarmenian:57f tlinebelow:1e6f toarmenian:569 tonebarextrahighmod:2e5 " +
            "tonebarextralowmod:2e9 tonebarhighmod:2e6 tonebarlowmod:2e8 tonebarmidmod:2e7 tonefive:1bd tonesix:185 tonetwo:1a8 tonos:384 topatakthai:e0f " +
            "totaothai:e15 tpalatalhook:1ab trademark:2122 tretroflexhook:288 triagdn:25bc triaglf:25c4 triagrt:25ba triagup:25b2 ts:2a6 tsadi:5e6 tsadihebrew:5e6 " +
            "tsecyrillic:446 tsere:5b5 tsere12:5b5 tsere1e:5b5 tsere2b:5b5 tserehebrew:5b5 tserenarrowhebrew:5b5 tserequarterhebrew:5b5 tserewidehebrew:5b5 " +
            "tshecyrillic:45b ttabengali:99f ttadeva:91f ttagujarati:a9f ttagurmukhi:a1f tteharabic:679 tthabengali:9a0 tthadeva:920 tthagujarati:aa0 " +
            "tthagurmukhi:a20 tturned:287 twelveroman:217b two:32 twoarabic:662 twobengali:9e8 twodeva:968 twodotenleader:2025 twodotleader:2025 twogujarati:ae8 " +
            "twogurmukhi:a68 twohackarabic:662 twoinferior:2082 twonumeratorbengali:9f5 twopersian:6f2 tworoman:2171 twostroke:1bb twosuperior:b2 twothai:e52 " +
            "twothirds:2154 u:75 uacute:fa ubar:289 ubengali:989 ubreve:16d ucaron:1d4 ucircumflex:fb ucircumflexbelow:1e77 ucyrillic:443 udattadeva:951 " +
            "udblacute:171 udblgrave:215 udeva:909 udieresis:fc udieresisacute:1d8 udieresisbelow:1e73 udieresiscaron:1da udieresiscyrillic:4f1 udieresisgrave:1dc " +
            "udieresismacron:1d6 udotbelow:1ee5 ugrave:f9 ugujarati:a89 ugurmukhi:a09 uhookabove:1ee7 uhorn:1b0 uhornacute:1ee9 uhorndotbelow:1ef1 uhorngrave:1eeb " +
            "uhornhookabove:1eed uhorntilde:1eef uhungarumlaut:171 uhungarumlautcyrillic:4f3 uinvertedbreve:217 ukcyrillic:479 umacron:16b umacroncyrillic:4ef " +
            "umacrondieresis:1e7b umatragurmukhi:a41 underscore:5f underscoredbl:2017 union:222a universal:2200 uogonek:173 upblock:2580 upperdothebrew:5c4 " +
            "upsilon:3c5 upsilondieresis:3cb upsilondieresistonos:3b0 upsilonlatin:28a upsilontonos:3cd uptackbelowcmb:31d uptackmod:2d4 uragurmukhi:a73 uring:16f " +
            "ushortcyrillic:45e ustraightcyrillic:4af ustraightstrokecyrillic:4b1 utilde:169 utildeacute:1e79 utildebelow:1e75 uubengali:98a uudeva:90a " +
            "uugujarati:a8a uugurmukhi:a0a uumatragurmukhi:a42 uuvowelsignbengali:9c2 uuvowelsigndeva:942 uuvowelsigngujarati:ac2 uvowelsignbengali:9c1 " +
            "uvowelsigndeva:941 uvowelsigngujarati:ac1 v:76 vadeva:935 vagujarati:ab5 vagurmukhi:a35 vav:5d5 vavhebrew:5d5 vavvavhebrew:5f0 vavyodhebrew:5f1 " +
            "vdotbelow:1e7f vecyrillic:432 veharabic:6a4 verticalbar:7c verticallineabovecmb:30d verticallinebelowcmb:329 verticallinelowmod:2cc " +
            "verticallinemod:2c8 vewarmenian:57e vhook:28b viramabengali:9cd viramadeva:94d viramagujarati:acd visargabengali:983 visargadeva:903 " +
            "visargagujarati:a83 voarmenian:578 vtilde:1e7d vturned:28c w:77 wacute:1e83 wawarabic:648 wawhamzaabovearabic:624 wcircumflex:175 wdieresis:1e85 " +
            "wdotaccent:1e87 wdotbelow:1e89 weierstrass:2118 wgrave:1e81 whitebullet:25e6 whitecircle:25cb whitecircleinverse:25d9 whitediamond:25c7 " +
            "whitediamondcontainingblacksmalldiamond:25c8 whitedownpointingsmalltriangle:25bf whitedownpointingtriangle:25bd whiteleftpointingsmalltriangle:25c3 " +
            "whiteleftpointingtriangle:25c1 whiterightpointingsmalltriangle:25b9 whiterightpointingtriangle:25b7 whitesmallsquare:25ab whitesquare:25a1 " +
            "whiteuppointingsmalltriangle:25b5 whiteuppointingtriangle:25b3 won:20a9 wowaenthai:e27 wring:1e98 wsuperior:2b7 wturned:28d wynn:1bf x:78 " +
            "xabovecmb:33d xdieresis:1e8d xdotaccent:1e8b xeharmenian:56d xi:3be xsuperior:2e3 y:79 yabengali:9af yacute:fd yadeva:92f yagujarati:aaf " +
            "yagurmukhi:a2f yamakkanthai:e4e yatcyrillic:463 ycircumflex:177 ydieresis:ff ydotaccent:1e8f ydotbelow:1ef5 yeharabic:64a yehbarreearabic:6d2 " +
            "yehhamzaabovearabic:626 yehthreedotsbelowarabic:6d1 yen:a5 yerahbenyomohebrew:5aa yerahbenyomolefthebrew:5aa yericyrillic:44b yerudieresiscyrillic:4f9 " +
            "yetivhebrew:59a ygrave:1ef3 yhook:1b4 yhookabove:1ef7 yiarmenian:575 yicyrillic:457 yiwnarmenian:582 yod:5d9 yodhebrew:5d9 yodyodhebrew:5f2 " +
            "yotgreek:3f3 yoyakthai:e22 yoyingthai:e0d ypogegrammeni:37a ypogegrammenigreekcmb:345 yr:1a6 yring:1e99 ysuperior:2b8 ytilde:1ef9 yturned:28e " +
            "yusbigcyrillic:46b yusbigiotifiedcyrillic:46d yuslittlecyrillic:467 yuslittleiotifiedcyrillic:469 yyabengali:9df yyadeva:95f z:7a zaarmenian:566 " +
            "zacute:17a zadeva:95b zagurmukhi:a5b zaharabic:638 zainarabic:632 zaqefgadolhebrew:595 zaqefqatanhebrew:594 zarqahebrew:598 zayin:5d6 zayinhebrew:5d6 " +
            "zcaron:17e zcircumflex:1e91 zcurl:291 zdot:17c zdotaccent:17c zdotbelow:1e93 zecyrillic:437 zedescendercyrillic:499 zedieresiscyrillic:4df zero:30 " +
            "zeroarabic:660 zerobengali:9e6 zerodeva:966 zerogujarati:ae6 zerogurmukhi:a66 zerohackarabic:660 zeroinferior:2080 zeropersian:6f0 zerosuperior:2070 " +
            "zerothai:e50 zerowidthnonjoiner:200c zerowidthspace:200b zeta:3b6 zhearmenian:56a zhebrevecyrillic:4c2 zhecyrillic:436 zhedescendercyrillic:497 " +
            "zhedieresiscyrillic:4dd zinorhebrew:5ae zlinebelow:1e95 zretroflexhook:290 zstroke:1b6 angbracketleftbig:2329 angbracketleftBig:2329 " +
            "angbracketleftbigg:2329 angbracketleftBigg:2329 angbracketrightBig:232a angbracketrightbig:232a angbracketrightBigg:232a angbracketrightbigg:232a " +
            "arrowhookleft:21aa arrowhookright:21a9 arrowlefttophalf:21bc arrowleftbothalf:21bd arrownortheast:2197 arrownorthwest:2196 arrowrighttophalf:21c0 " +
            "arrowrightbothalf:21c1 arrowsoutheast:2198 arrowsouthwest:2199 backslashbig:2216 backslashBig:2216 backslashBigg:2216 backslashbigg:2216 bardbl:2016 " +
            "braceleftBig:7b braceleftbig:7b braceleftbigg:7b braceleftBigg:7b bracerightBig:7d bracerightbig:7d bracerightbigg:7d bracerightBigg:7d " +
            "bracketleftbig:5b bracketleftBig:5b bracketleftbigg:5b bracketleftBigg:5b bracketrightBig:5d bracketrightbig:5d bracketrightbigg:5d " +
            "bracketrightBigg:5d ceilingleftbig:2308 ceilingleftBig:2308 ceilingleftBigg:2308 ceilingleftbigg:2308 ceilingrightbig:2309 ceilingrightBig:2309 " +
            "ceilingrightbigg:2309 ceilingrightBigg:2309 circledotdisplay:2299 circledottext:2299 circlemultiplydisplay:2297 circlemultiplytext:2297 " +
            "circleplusdisplay:2295 circleplustext:2295 contintegraldisplay:222e contintegraltext:222e coproductdisplay:2210 coproducttext:2210 floorleftBig:230a " +
            "floorleftbig:230a floorleftbigg:230a floorleftBigg:230a floorrightbig:230b floorrightBig:230b floorrightBigg:230b floorrightbigg:230b hatwide:302 " +
            "hatwider:302 hatwidest:302 intercal:1d40 integraldisplay:222b integraltext:222b intersectiondisplay:22c2 intersectiontext:22c2 logicalanddisplay:2227 " +
            "logicalandtext:2227 logicalordisplay:2228 logicalortext:2228 parenleftBig:28 parenleftbig:28 parenleftBigg:28 parenleftbigg:28 parenrightBig:29 " +
            "parenrightbig:29 parenrightBigg:29 parenrightbigg:29 prime:2032 productdisplay:220f producttext:220f radicalbig:221a radicalBig:221a radicalBigg:221a " +
            "radicalbigg:221a radicalbt:221a radicaltp:221a radicalvertex:221a slashbig:2f slashBig:2f slashBigg:2f slashbigg:2f summationdisplay:2211 " +
            "summationtext:2211 tildewide:2dc tildewider:2dc tildewidest:2dc uniondisplay:22c3 unionmultidisplay:228e unionmultitext:228e unionsqdisplay:2294 " +
            "unionsqtext:2294 uniontext:22c3 vextenddouble:2225 vextendsingle:2223 space:20 a71:25cf a73:25a0 a76:25b2 a77:25bc a78:25c6 a81:25d7 a161:2192 " +
            "a163:2194 a164:2195";
    }
}
