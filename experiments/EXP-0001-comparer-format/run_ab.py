"""
XML-vs-JSON A/B for the Reflect tuning hypotheses (2026-06-17).

Two questions, isolated:

  Test A - the divergence comparer's "over-fires on emphasis" problem.
    3 arms, factorial (so we separate WORDING from FORMAT):
      A1  json + original prompt          (= ember today, the baseline)
      A2  json + improved prompt          (contradiction-not-tone, +kind)
      A3  xml  + improved prompt          (same wording, xml output)
    A1->A2 isolates the wording change; A2->A3 isolates the format change.
    Same two FROZEN recaps feed every arm, so only the named variable moves.

  Test B - the 30B's hallucination (it invented an ember->leopard dependency).
    2 arms on the 30B planner:
      B1  plain markdown recap            (= ember today)
      B2  xml recap, every claim must cite a <from> hash/path from evidence
    Auto-metric: for B2, what fraction of <from> citations actually appear
    in the evidence (fabricated-citation detector). B1 is read by hand.

All raw outputs are written to results/ so nothing is lost and the operator
can verify. stdout stays ASCII (Windows cp1252).
"""

import json, os, re, sys, time, urllib.request
import xml.etree.ElementTree as ET

FACADE  = "http://127.0.0.1:8090/v1/chat/completions"
PLANNER = "vllama-planner"   # 30B, slot-a
CRITIC  = "vllama-critic"    # 14B, slot-b
HERE    = os.path.dirname(os.path.abspath(__file__))
OUTDIR  = os.path.join(HERE, "results")
os.makedirs(OUTDIR, exist_ok=True)

SMOKE = os.environ.get("ABTEST_SMOKE") == "1"
K     = int(os.environ.get("ABTEST_K", "1" if SMOKE else "3"))

# ---- prompts (recap authors) ------------------------------------------------

RECAP_PLAIN = (
    "You are an independent engineering-journal writer for a solo developer's "
    "multi-repo workspace (the \"constellation\"). You are given structured evidence "
    "of a working period: per-repo commits, changed files, and code symbols.\n\n"
    "Write a concise recap in Markdown with exactly these sections:\n"
    "1. **What happened** - per repo, concrete and specific.\n"
    "2. **Threads & risks** - cross-repo connections, half-done work, follow-ups.\n"
    "3. **Open questions** - things the evidence cannot settle.\n\n"
    "Ground every statement in the evidence. Do not invent files, symbols, or motives. "
    "If the evidence is thin, say so plainly. At most 400 words."
)

RECAP_XML_CITE = (
    "You are an independent engineering-journal writer for a solo developer's "
    "multi-repo workspace (the \"constellation\"). You are given structured evidence: "
    "per-repo commits, changed files, and code symbols.\n\n"
    "Write the recap as XML in EXACTLY this shape:\n"
    "<recap>\n"
    "  <repo name=\"...\">\n"
    "    <claim>\n"
    "      <statement>one concrete thing that happened</statement>\n"
    "      <from>a commit hash or file path that appears VERBATIM in the evidence</from>\n"
    "    </claim>\n"
    "  </repo>\n"
    "  <threads><thread>cross-repo connection or risk grounded in evidence</thread></threads>\n"
    "  <open-questions><question>something the evidence cannot settle</question></open-questions>\n"
    "</recap>\n\n"
    "HARD RULE: every <statement> MUST carry a <from> that cites a hash or path present "
    "verbatim in the evidence. If you cannot cite it, do not write it. Never invent "
    "cross-repo dependencies. Output ONLY the XML."
)

# ---- prompts (comparer) -----------------------------------------------------

CMP_JSON_ORIG = (
    "You compare two independently written recaps of the same engineering evidence. "
    "Identify where they agree and where they meaningfully diverge - different claims, "
    "different emphasis on risk, or facts one mentions that the other omits. Ignore "
    "phrasing differences.\n\n"
    "Respond with ONLY a JSON object - no prose, no markdown fences:\n"
    "{ \"agreements\": [\"<shared claim>\"], \"divergences\": [ { \"topic\": \"<subject>\", "
    "\"aSays\": \"<recap A's position>\", \"bSays\": \"<recap B's position>\" } ] }\n"
    "Keep each entry to one sentence. Empty arrays are valid."
)

CMP_JSON_IMPROVED = (
    "You compare two independently written recaps of the same engineering evidence. "
    "Report ONLY genuine divergences: one recap asserts something the other CONTRADICTS, "
    "or states a load-bearing fact the other OMITS. Do NOT report differences that are "
    "merely tone, emphasis, confidence, or wording - those are not divergences.\n\n"
    "Respond with ONLY a JSON object - no prose, no fences:\n"
    "{ \"agreements\": [\"<shared claim>\"], \"divergences\": [ { \"topic\": \"<subject>\", "
    "\"kind\": \"contradiction|omission\", \"aSays\": \"<A's position>\", \"bSays\": \"<B's position>\" } ] }\n"
    "Keep each entry to one sentence. Empty arrays are valid."
)

CMP_XML_IMPROVED = (
    "You compare two independently written recaps of the same engineering evidence. "
    "Report ONLY genuine divergences: one recap asserts something the other CONTRADICTS, "
    "or states a load-bearing fact the other OMITS. Do NOT report differences that are "
    "merely tone, emphasis, confidence, or wording - those are not divergences.\n\n"
    "Respond with ONLY this XML, nothing else:\n"
    "<comparison>\n"
    "  <agreements><item>shared claim</item></agreements>\n"
    "  <divergences>\n"
    "    <divergence>\n"
    "      <topic>subject</topic>\n"
    "      <kind>contradiction|omission</kind>\n"
    "      <a>A's position</a>\n"
    "      <b>B's position</b>\n"
    "    </divergence>\n"
    "  </divergences>\n"
    "</comparison>\n"
    "Empty <agreements/> or <divergences/> are valid."
)

# ---- http -------------------------------------------------------------------

def chat(model, system, user, max_tokens, temperature):
    body = {
        "model": model,
        "messages": [{"role": "system", "content": system},
                     {"role": "user", "content": user}],
        "max_tokens": max_tokens, "temperature": temperature, "stream": False,
    }
    data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(FACADE, data=data,
                                 headers={"Content-Type": "application/json"})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=300) as resp:
            j = json.loads(resp.read().decode("utf-8"))
        return j["choices"][0]["message"]["content"], time.time() - t0, None
    except Exception as e:
        return None, time.time() - t0, str(e)

# ---- parsing ----------------------------------------------------------------

def parse_json_cmp(text):
    if text is None:
        return None
    s, e = text.find("{"), text.rfind("}")
    if s < 0 or e <= s:
        return None
    try:
        o = json.loads(text[s:e + 1])
        return {"agreements": len(o.get("agreements", [])),
                "divergences": len(o.get("divergences", [])),
                "div_list": o.get("divergences", [])}
    except Exception:
        return None

def parse_xml_cmp(text):
    if text is None:
        return None
    s, e = text.find("<comparison"), text.rfind("</comparison>")
    if s < 0 or e <= s:
        return None
    try:
        root = ET.fromstring(text[s:e + len("</comparison>")])
        ags = root.findall(".//agreements/item")
        divs = root.findall(".//divergences/divergence")
        dl = []
        for d in divs:
            dl.append({"topic": (d.findtext("topic") or "").strip(),
                       "kind": (d.findtext("kind") or "").strip(),
                       "a": (d.findtext("a") or "").strip(),
                       "b": (d.findtext("b") or "").strip()})
        return {"agreements": len(ags), "divergences": len(divs), "div_list": dl}
    except Exception:
        return None

def check_citations(xml_text, evidence):
    """For an xml-cite recap: fraction of <from> tags whose text appears in evidence."""
    if xml_text is None:
        return None
    froms = re.findall(r"<from>(.*?)</from>", xml_text, re.S)
    if not froms:
        return {"total": 0, "valid": 0, "fabricated": []}
    valid, fabricated = 0, []
    for f in froms:
        token = f.strip()
        # cite is "valid" if the hash/path (or its first whitespace token) is in evidence
        head = token.split()[0] if token.split() else token
        if head and head in evidence:
            valid += 1
        else:
            fabricated.append(token)
    return {"total": len(froms), "valid": valid, "fabricated": fabricated}

def save(name, text):
    with open(os.path.join(OUTDIR, name), "w", encoding="utf-8") as f:
        f.write(text if text is not None else "(none)")

# ---- evidence ---------------------------------------------------------------

def load_evidence():
    if SMOKE:
        return ("# Evidence - smoke\n### ember - 1 commit\n- d653498 Reflect R1+R2\n"
                "Files:\n- src/Ember/Reflect/ReflectRunner.cs\n")
    path = os.path.join(HERE, "dryrun.txt")
    raw = open(path, encoding="utf-8", errors="replace").read()
    s = raw.find("# Evidence")
    e = raw.find("status:")
    if s < 0:
        raise SystemExit("could not find '# Evidence' marker in dryrun.txt")
    return raw[s:(e if e > s else len(raw))].strip()

# ---- run --------------------------------------------------------------------

def main():
    evidence = load_evidence()
    save("evidence.txt", evidence)
    rt = 64 if SMOKE else 700
    ct = 64 if SMOKE else 500
    summary = {"smoke": SMOKE, "K": K, "evidence_chars": len(evidence), "testA": {}, "testB": {}}

    print("=== A/B: XML vs JSON  (smoke=%s, K=%d, evidence=%d chars) ===" % (SMOKE, K, len(evidence)))

    # ---- fixtures: one Recap A (30B) + one Recap B (14B), frozen for Test A
    print("\n[fixtures] generating frozen Recap A (30B) + Recap B (14B) ...")
    if SMOKE:
        recapA = "ember shipped Reflect. leopard depends on nothing here."
        recapB = "ember added a recap subsystem. No cross-repo dependency seen."
        ga = gb = 0.0
    else:
        recapA, ga, ea = chat(PLANNER, RECAP_PLAIN, "Evidence:\n\n" + evidence, rt, 0.6)
        recapB, gb, eb = chat(CRITIC,  RECAP_PLAIN, "Evidence:\n\n" + evidence, rt, 0.6)
    save("fixture_recapA_30b.txt", recapA)
    save("fixture_recapB_14b.txt", recapB)
    print("  recapA 30B %.1fs (%s)  recapB 14B %.1fs (%s)" % (
        ga, "ok" if recapA else "FAIL", gb, "ok" if recapB else "FAIL"))

    cmp_user = "Recap A:\n%s\n\nRecap B:\n%s" % (recapA, recapB)
    arms = [("A1_json_orig", CMP_JSON_ORIG, parse_json_cmp),
            ("A2_json_improved", CMP_JSON_IMPROVED, parse_json_cmp),
            ("A3_xml_improved", CMP_XML_IMPROVED, parse_xml_cmp)]

    print("\n[Test A] comparer arms (temp 0):")
    for arm, sysp, parser in arms:
        runs = []
        for k in range(K):
            txt, dt, err = chat(CRITIC, sysp, cmp_user, ct, 0.0)
            save("%s_run%d.txt" % (arm, k), txt if txt else "ERR: %s" % err)
            p = parser(txt)
            runs.append({"parse_ok": p is not None,
                         "agreements": p["agreements"] if p else None,
                         "divergences": p["divergences"] if p else None,
                         "latency_s": round(dt, 1)})
        ok = sum(1 for r in runs if r["parse_ok"])
        divs = [r["divergences"] for r in runs if r["divergences"] is not None]
        summary["testA"][arm] = {"parse_ok": "%d/%d" % (ok, K),
                                 "divergences": divs,
                                 "mean_div": round(sum(divs) / len(divs), 1) if divs else None,
                                 "runs": runs}
        print("  %-18s parse %d/%d  divergences %s" % (arm, ok, K, divs))

    # ---- Test B: recap grounding on the 30B
    print("\n[Test B] 30B recap grounding (plain vs xml-cite):")
    b = {"B1_plain": [], "B2_xml_cite": []}
    for k in range(K):
        t1, d1, e1 = chat(PLANNER, RECAP_PLAIN, "Evidence:\n\n" + evidence, rt, 0.6)
        save("B1_plain_run%d.txt" % k, t1 if t1 else "ERR: %s" % e1)
        b["B1_plain"].append({"ok": t1 is not None, "latency_s": round(d1, 1)})

        t2, d2, e2 = chat(PLANNER, RECAP_XML_CITE, "Evidence:\n\n" + evidence, rt, 0.6)
        save("B2_xml_cite_run%d.txt" % k, t2 if t2 else "ERR: %s" % e2)
        cites = check_citations(t2, evidence)
        b["B2_xml_cite"].append({"ok": t2 is not None, "latency_s": round(d2, 1),
                                 "citations": cites})
        if cites:
            print("  run%d xml-cite: %d/%d citations valid, %d fabricated" % (
                k, cites["valid"], cites["total"], len(cites["fabricated"])))
    summary["testB"] = b

    with open(os.path.join(OUTDIR, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2)
    print("\n=== done. raw outputs + summary.json in %s ===" % OUTDIR)

if __name__ == "__main__":
    main()
