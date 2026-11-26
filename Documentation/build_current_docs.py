from __future__ import annotations

import re
from pathlib import Path
from typing import Iterable, Sequence

from docx import Document
from docx.enum.section import WD_SECTION
from docx.enum.table import WD_CELL_VERTICAL_ALIGNMENT, WD_ROW_HEIGHT_RULE
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Inches, Pt, RGBColor


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "Documentation"
IMAGE = ROOT / "ButchCrawler.Operator" / "Assets" / "western-about-collage.png"
BROCHURE_MAIN_SCREEN = OUT / "Assets" / "history-crawler-main-screen.png"
ASSET = OUT / "Assets"

NAVY = "17324D"
BLUE = "0B7A86"
PALE_BLUE = "EAF3FA"
PALE_GOLD = "F8F0E2"
GOLD = "B47A32"
RED = "B4232D"
GREEN = "137A43"
GRAY = "5C6C82"
LIGHT_GRAY = "D9E1EA"
WHITE = "FFFFFF"
BLACK = "111111"


def rgb(value: str) -> RGBColor:
    return RGBColor.from_string(value)


def set_cell_shading(cell, fill: str) -> None:
    tc_pr = cell._tc.get_or_add_tcPr()
    shd = tc_pr.find(qn("w:shd"))
    if shd is None:
        shd = OxmlElement("w:shd")
        tc_pr.append(shd)
    shd.set(qn("w:fill"), fill)


def set_cell_border(cell, color: str = LIGHT_GRAY, size: str = "6") -> None:
    tc_pr = cell._tc.get_or_add_tcPr()
    borders = tc_pr.first_child_found_in("w:tcBorders")
    if borders is None:
        borders = OxmlElement("w:tcBorders")
        tc_pr.append(borders)
    for edge in ("top", "left", "bottom", "right", "insideH", "insideV"):
        element = borders.find(qn(f"w:{edge}"))
        if element is None:
            element = OxmlElement(f"w:{edge}")
            borders.append(element)
        element.set(qn("w:val"), "single")
        element.set(qn("w:sz"), size)
        element.set(qn("w:color"), color)


def set_cell_margins(cell, top=80, start=100, bottom=80, end=100) -> None:
    tc = cell._tc
    tc_pr = tc.get_or_add_tcPr()
    tc_mar = tc_pr.first_child_found_in("w:tcMar")
    if tc_mar is None:
        tc_mar = OxmlElement("w:tcMar")
        tc_pr.append(tc_mar)
    for margin, value in (("top", top), ("start", start), ("bottom", bottom), ("end", end)):
        node = tc_mar.find(qn(f"w:{margin}"))
        if node is None:
            node = OxmlElement(f"w:{margin}")
            tc_mar.append(node)
        node.set(qn("w:w"), str(value))
        node.set(qn("w:type"), "dxa")


def prevent_row_split(row) -> None:
    tr_pr = row._tr.get_or_add_trPr()
    cant_split = OxmlElement("w:cantSplit")
    tr_pr.append(cant_split)


def repeat_table_header(row) -> None:
    tr_pr = row._tr.get_or_add_trPr()
    tbl_header = OxmlElement("w:tblHeader")
    tbl_header.set(qn("w:val"), "true")
    tr_pr.append(tbl_header)


def add_field(paragraph, instruction: str) -> None:
    run = paragraph.add_run()
    begin = OxmlElement("w:fldChar")
    begin.set(qn("w:fldCharType"), "begin")
    instr = OxmlElement("w:instrText")
    instr.set(qn("xml:space"), "preserve")
    instr.text = instruction
    separate = OxmlElement("w:fldChar")
    separate.set(qn("w:fldCharType"), "separate")
    end = OxmlElement("w:fldChar")
    end.set(qn("w:fldCharType"), "end")
    run._r.extend((begin, instr, separate, end))


def set_last_picture_alt(doc: Document, title: str, description: str) -> None:
    """Add useful alternate text to the most recently inserted inline picture."""
    if not doc.inline_shapes:
        return
    doc_pr = doc.inline_shapes[-1]._inline.docPr
    doc_pr.set("title", title)
    doc_pr.set("descr", description)


def configure_document(doc: Document, short_title: str) -> None:
    section = doc.sections[0]
    section.top_margin = Inches(0.7)
    section.bottom_margin = Inches(0.65)
    section.left_margin = Inches(0.75)
    section.right_margin = Inches(0.75)
    section.header_distance = Inches(0.28)
    section.footer_distance = Inches(0.28)
    section.different_first_page_header_footer = False

    styles = doc.styles
    normal = styles["Normal"]
    normal.font.name = "Arial"
    normal.font.size = Pt(10.5)
    normal.font.color.rgb = rgb(BLACK)
    normal.paragraph_format.space_after = Pt(5)
    normal.paragraph_format.line_spacing = 1.08

    for style_name, size in (("Title", 28), ("Subtitle", 14), ("Heading 1", 20), ("Heading 2", 14.5), ("Heading 3", 12)):
        style = styles[style_name]
        style.font.name = "Arial"
        style.font.size = Pt(size)
        style.font.color.rgb = rgb(BLACK)
        if style_name.startswith("Heading"):
            style.font.bold = True
            style.paragraph_format.keep_with_next = True
            style.paragraph_format.space_before = Pt(10 if style_name != "Heading 1" else 14)
            style.paragraph_format.space_after = Pt(5)
    title_p_pr = styles["Title"].element.get_or_add_pPr()
    title_border = title_p_pr.find(qn("w:pBdr"))
    if title_border is not None:
        title_p_pr.remove(title_border)

    settings = doc.settings.element
    update = OxmlElement("w:updateFields")
    update.set(qn("w:val"), "true")
    settings.append(update)


def set_core_properties(doc: Document, title: str, subject: str) -> None:
    props = doc.core_properties
    props.title = title
    props.subject = subject
    props.author = "History Crawler"
    props.keywords = "history research, web crawler, OCR, PDF, structured data, security"
    props.comments = "Current feature documentation for History Crawler."


def configure_content_section(section, short_title: str) -> None:
    section.top_margin = Inches(0.7)
    section.bottom_margin = Inches(0.65)
    section.left_margin = Inches(0.75)
    section.right_margin = Inches(0.75)
    section.header_distance = Inches(0.28)
    section.footer_distance = Inches(0.28)
    section.header.is_linked_to_previous = False
    section.footer.is_linked_to_previous = False
    p = section.header.paragraphs[0]
    p.text = short_title
    p.alignment = WD_ALIGN_PARAGRAPH.RIGHT
    p.runs[0].font.size = Pt(8.5)
    p.runs[0].font.color.rgb = rgb(GRAY)
    p = section.footer.paragraphs[0]
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = p.add_run(f"{short_title}  ·  ")
    run.font.size = Pt(8.5)
    run.font.color.rgb = rgb(GRAY)
    add_field(p, "PAGE")


def add_cover(doc: Document, title: str, subtitle: str, edition: str, audience: str, short_title: str,
              copyright_text: str | None = None) -> None:
    if IMAGE.exists():
        doc.add_picture(str(IMAGE), width=Inches(7.0))
        set_last_picture_alt(
            doc,
            "History Crawler",
            "History Crawler cover artwork showing historical research materials.",
        )
        doc.paragraphs[-1].alignment = WD_ALIGN_PARAGRAPH.CENTER
    p = doc.add_paragraph(style="Title")
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.add_run(title)
    p = doc.add_paragraph(style="Subtitle")
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.add_run(subtitle)
    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    r = p.add_run(edition)
    r.bold = True
    r.font.color.rgb = rgb(BLUE)
    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    r = p.add_run(audience)
    r.font.size = Pt(10)
    r.font.color.rgb = rgb(GRAY)
    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    r = p.add_run("Program name: History Crawler")
    r.font.size = Pt(9)
    r.font.color.rgb = rgb(GRAY)
    if copyright_text:
        p = doc.add_paragraph()
        p.alignment = WD_ALIGN_PARAGRAPH.CENTER
        r = p.add_run(copyright_text)
        r.font.size = Pt(9)
        r.font.color.rgb = rgb(GRAY)
    configure_content_section(doc.add_section(WD_SECTION.NEW_PAGE), short_title)


def add_heading(doc: Document, text: str, level: int = 1) -> None:
    # Headings stay plain and easy to scan in Word's Navigation pane.
    plain_text = re.sub(r"[^A-Za-z0-9 ]+", " ", text)
    plain_text = re.sub(r"\s+", " ", plain_text).strip()
    doc.add_heading(plain_text, level=level)


def add_para(doc: Document, text: str, bold_prefix: str | None = None, italic=False) -> None:
    p = doc.add_paragraph()
    if bold_prefix and text.startswith(bold_prefix):
        p.add_run(bold_prefix).bold = True
        p.add_run(text[len(bold_prefix):])
    else:
        r = p.add_run(text)
        r.italic = italic


def add_bullets(doc: Document, items: Iterable[str], level: int = 0) -> None:
    style = "List Bullet" if level == 0 else "List Bullet 2"
    for item in items:
        p = doc.add_paragraph(style=style)
        p.paragraph_format.space_after = Pt(3)
        p.add_run(item)


def add_steps(doc: Document, steps: Iterable[str]) -> None:
    for number, step in enumerate(steps, start=1):
        p = doc.add_paragraph()
        p.paragraph_format.left_indent = Inches(0.30)
        p.paragraph_format.first_line_indent = Inches(-0.25)
        p.paragraph_format.space_after = Pt(4)
        number_run = p.add_run(f"{number}. ")
        number_run.bold = True
        p.add_run(step)


def add_howto(doc: Document, title: str, steps: Iterable[str]) -> None:
    """Keep each friendly procedure together in a clearly separated card."""
    step_list = list(steps)
    table = doc.add_table(rows=len(step_list) + 1, cols=1)

    heading_cell = table.cell(0, 0)
    set_cell_shading(heading_cell, PALE_BLUE)
    set_cell_border(heading_cell, LIGHT_GRAY, "6")
    set_cell_margins(heading_cell, top=120, start=160, bottom=70, end=160)
    prevent_row_split(table.rows[0])
    heading = heading_cell.paragraphs[0]
    heading.paragraph_format.space_after = Pt(6)
    heading_run = heading.add_run(title)
    heading_run.bold = True
    heading_run.font.name = "Arial"
    heading_run.font.size = Pt(14.5)

    for number, step in enumerate(step_list, start=1):
        row = table.rows[number]
        cell = row.cells[0]
        set_cell_shading(cell, PALE_BLUE)
        set_cell_border(cell, LIGHT_GRAY, "6")
        set_cell_margins(cell, top=40, start=160, bottom=50, end=160)
        prevent_row_split(row)
        p = cell.paragraphs[0]
        p.paragraph_format.space_after = Pt(0)
        number_run = p.add_run(f"{number}. ")
        number_run.bold = True
        p.add_run(step)

    spacer = doc.add_paragraph()
    spacer.paragraph_format.space_after = Pt(2)


def add_note(doc: Document, heading: str, text: str, kind: str = "info") -> None:
    p = doc.add_paragraph()
    p.paragraph_format.left_indent = Inches(0.18)
    p.paragraph_format.right_indent = Inches(0.18)
    p.paragraph_format.space_before = Pt(6)
    p.paragraph_format.space_after = Pt(7)
    r = p.add_run(heading + ": ")
    r.bold = True
    r.font.color.rgb = rgb(RED if kind == "warning" else BLUE)
    p.add_run(text)
    p_pr = p._p.get_or_add_pPr()
    shd = OxmlElement("w:shd")
    shd.set(qn("w:fill"), PALE_GOLD if kind == "warning" else PALE_BLUE)
    p_pr.append(shd)


def add_table(doc: Document, headers: Sequence[str], rows: Sequence[Sequence[str]], widths: Sequence[float] | None = None) -> None:
    table = doc.add_table(rows=1, cols=len(headers))
    table.autofit = False
    table.style = "Table Grid"
    header = table.rows[0]
    repeat_table_header(header)
    prevent_row_split(header)
    for index, heading in enumerate(headers):
        cell = header.cells[index]
        set_cell_shading(cell, NAVY)
        set_cell_border(cell)
        set_cell_margins(cell)
        cell.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.CENTER
        p = cell.paragraphs[0]
        p.paragraph_format.space_after = Pt(0)
        r = p.add_run(heading)
        r.bold = True
        r.font.color.rgb = rgb(WHITE)
        r.font.size = Pt(9.2)
        if widths:
            cell.width = Inches(widths[index])
    for row_index, values in enumerate(rows):
        row = table.add_row()
        prevent_row_split(row)
        row.height_rule = WD_ROW_HEIGHT_RULE.AUTO
        for index, value in enumerate(values):
            cell = row.cells[index]
            set_cell_shading(cell, PALE_BLUE if row_index % 2 == 0 else WHITE)
            set_cell_border(cell)
            set_cell_margins(cell)
            cell.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.TOP
            p = cell.paragraphs[0]
            p.paragraph_format.space_after = Pt(0)
            r = p.add_run(str(value))
            r.font.size = Pt(9)
            if widths:
                cell.width = Inches(widths[index])
    doc.add_paragraph().paragraph_format.space_after = Pt(1)


def add_section_break(doc: Document) -> None:
    p = doc.add_paragraph()
    p.paragraph_format.space_after = Pt(3)


def add_figure(doc: Document, filename: str, caption: str, width: float = 6.8) -> None:
    path = ASSET / filename
    if not path.exists():
        return
    image_paragraph = doc.add_paragraph()
    image_paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
    image_paragraph.paragraph_format.keep_with_next = True
    image_paragraph.paragraph_format.space_before = Pt(5)
    image_paragraph.paragraph_format.space_after = Pt(3)
    image_paragraph.add_run().add_picture(str(path), width=Inches(width))
    set_last_picture_alt(doc, caption, f"Example History Crawler screen: {caption}.")
    caption_paragraph = doc.add_paragraph(style="Caption")
    caption_paragraph.alignment = WD_ALIGN_PARAGRAPH.CENTER
    caption_paragraph.paragraph_format.space_after = Pt(8)
    caption_paragraph.add_run(caption)


def build_manual() -> Path:
    doc = Document()
    configure_document(doc, "History Crawler · User Manual")
    set_core_properties(doc, "History Crawler User Manual", "Complete, friendly operating guide")
    add_cover(
        doc,
        "History Crawler User Manual",
        "A friendly, complete guide to collecting, reading, organizing, and searching historical sources",
        "Current feature edition · November 16, 2026",
        "Written for historians, professors, students, genealogists, archivists, and first-time computer users",
        "History Crawler · User Manual",
        "Copyright © Tom Affholter, 2026. All rights reserved.",
    )

    add_heading(doc, "Contents", 1)
    add_para(doc, "Use this list to find a topic quickly. In Word, the Navigation pane can also jump to any heading.")
    add_bullets(doc, [
        "1. Start here: what History Crawler does",
        "2. Installation and the five-step first-time guide",
        "3. The main screen, every counter, and every activity tab",
        "4. How seeds, discovered links, local files, retries, and stopping work",
        "5. Search collection and Boolean search",
        "6. Every Settings menu choice",
        "7. Seed URLs and Seed Keywords",
        "8. Problem seeds, cleanup, retries, and human verification",
        "9. Free historical newspaper archives",
        "10. Supported files, OCR, PDF, images, audio, and structured data",
        "11. Optional AI tools and costs",
        "12. Saved information, provenance, logs, and privacy",
        "13. Donations, subscriptions, suggestions, and saved responses",
        "14. Security protections",
        "15. Preparing a clean starter database and release package",
        "16. Troubleshooting",
        "17. Keyboard and mouse reference",
        "Appendix A. How to…",
        "Appendix B. Current changes and removed features",
    ])
    add_note(doc, "Name used in this guide", "The product is called History Crawler. Older internal project filenames may still use a prototype name, but users should use the History Crawler name.")

    doc.add_page_break()
    add_heading(doc, "1. Start here", 1)
    add_heading(doc, "What History Crawler is", 2)
    add_para(doc, "History Crawler is a Windows research application that follows approved seed websites, saves readable source material, finds related links, reads supported files, records keyword matches, and lets you search the collected text from one place. It is designed to build a research collection with source addresses and processing history—not to replace scholarly judgment.")
    add_heading(doc, "What it can do without AI", 2)
    add_bullets(doc, [
        "Crawl public HTTP and HTTPS sources while respecting robots rules and configured delays.",
        "Read HTML pages, saved web archives, text documents, Word files, PDFs, images, spreadsheets, databases, structured datasets, and safe entries inside ZIP files.",
        "Run OCR on images and scanned PDF pages when the optional OCR tools are installed.",
        "Save source text, titles, addresses, dates, status codes, keyword matches, processing notes, and structured-data provenance.",
        "Search the saved collection locally with ordinary words or Boolean logic. Local search does not use OpenAI credits.",
        "Review problem sources, schedule retries, disable or delete bad seeds, and temporarily verify legitimate human-challenge sites.",
        "Search the free Library of Congress Chronicling America collection by active seed keywords and selected states.",
    ])
    add_heading(doc, "What is optional", 2)
    add_table(doc, ["Optional item", "What it adds", "Cost or account"], [
        ["Tesseract OCR", "Reads words from images and scanned pages.", "Free third-party installation."],
        ["ImageMagick + Ghostscript", "Prepares difficult images and renders scanned PDF pages for OCR.", "Separate third-party installations and licenses."],
        ["Microsoft Access Database Engine (64-bit)", "Reads .mdb, .accdb, and legacy .xls data in read-only mode.", "Free Microsoft component; version must match the app architecture."],
        ["FFmpeg", "Converts supported audio before transcription.", "Free third-party installation."],
        ["OpenAI API key", "Manual AI research, handwriting help, optional audio transcription, and optional semantic indexing.", "API use may incur charges. AI starts off."],
    ], [1.6, 3.0, 2.0])
    add_heading(doc, "What History Crawler deliberately does not do", 2)
    add_bullets(doc, [
        "It does not bypass paywalls, account permissions, CAPTCHA, Cloudflare Turnstile, or a website’s terms of use.",
        "It does not execute downloaded programs, macros, SQL scripts, or database commands found in collected files.",
        "It does not store website usernames or passwords for automatic sign-in.",
        "It does not attach itself to an arbitrary personal Edge window or copy that browser profile.",
        "It does not automate Newspapers.com or Ancestry sign-in. That feature was removed because protected login sessions could not be transferred reliably or safely into the crawler.",
        "It cannot promise that every site is reachable or that every historical scan contains readable OCR text.",
    ])

    add_section_break(doc)
    add_heading(doc, "2. Installation and first-time setup", 1)
    add_heading(doc, "Before opening the program", 2)
    add_bullets(doc, [
        "Use a current 64-bit version of Windows.",
        "Install Microsoft SQL Server LocalDB. The first-time guide can install the bundled clean starter database only when the configured database does not already exist.",
        "Keep Windows Security or another reputable antivirus product enabled and updated.",
        "For OCR and media support, install only the optional tools you intend to use and keep their normal, trusted installation folders.",
    ])
    add_heading(doc, "The five setup steps", 2)
    add_steps(doc, [
        "Welcome: read what setup will and will not do. Setup does not begin a crawl or spend AI credits.",
        "Research choices: choose a broad region, enter optional research notes, and decide whether to use the free Library of Congress newspaper search.",
        "Research folder: choose the folder that contains your local research files. Local scanning remains off unless you select it.",
        "System check: confirm Windows and the Microsoft SQL database; review optional Tesseract, ImageMagick, Ghostscript, and FFmpeg status.",
        "Ready: review the summary and choose Save and finish. The main window opens with the crawler stopped.",
    ])
    add_note(doc, "Existing database", "First-time setup never overwrites an existing configured database. When the database already exists, it is kept. When it is missing and a clean starter backup is bundled, the program restores that separate starter database into the current user’s local application-data folder.", "warning")
    add_heading(doc, "Safe defaults", 2)
    add_para(doc, "The recommended setup leaves AI, automatic audio transcription, and semantic indexing off. It enables conservative robots handling, one connection at a time per website, and a polite delay. The crawler still starts only when you choose Start crawler.")
    add_heading(doc, "Opening the setup guide later", 2)
    add_para(doc, "Choose Settings > First-time setup guide. This lets you revisit the same choices without pretending the program is newly installed.")

    add_section_break(doc)
    add_heading(doc, "3. The main screen", 1)
    add_figure(doc, "operator.png", "Figure 1  The main History Crawler workspace while the crawler is stopped")
    add_heading(doc, "Top-right status badge", 2)
    add_table(doc, ["Badge", "Meaning"], [
        ["Crawler stopped", "No crawler process is running."],
        ["Starting…", "The worker process is launching and checking prerequisites."],
        ["Crawler Running", "The worker reported a successful start and is processing work."],
        ["Stopping…", "A stop request was sent; current work is winding down."],
        ["Crawler stopped unexpectedly / cannot start", "The worker exited or failed startup. Read the visible activity and Log files for the cause."],
    ], [2.0, 4.7])
    add_heading(doc, "Main buttons", 2)
    add_table(doc, ["Button", "What it does"], [
        ["Start crawler", "Starts the worker. The button becomes unavailable while running."],
        ["Stop", "Requests an orderly stop. Wait for the badge to return to Crawler stopped before changing database-sensitive settings."],
        ["Search collection…", "Searches saved information locally. It does not use OpenAI credits."],
        ["Settings ▼", "Opens all setup, seed, problem, newspaper, key, log, reset, and starter-package choices."],
        ["Support ▼", "Opens Donations, Subscribe, Send a suggestion, and Received responses."],
        ["About", "Shows the version, compiled date, seed totals, discovered-URL total, creator information, copyright, support email, local AI status, and links to API billing/usage pages."],
        ["Exit", "Closes the window. If the crawler is running, the program asks before ending it."],
    ], [1.55, 5.15])
    add_heading(doc, "Run counters", 2)
    add_table(doc, ["Counter", "Exactly what it means"], [
        ["URLs started", "Fetch attempts begun during this run. A retry can cause the same address to be counted again."],
        ["HTTP responses", "HTTP responses received during this run."],
        ["Pages retrieved successfully", "Sources successfully fetched and accepted for processing during this run."],
        ["Failed attempts", "Failed fetch or processing attempts during this run. A later retry may still succeed."],
        ["Links queued", "New pieces of crawl work queued during this run, including links discovered from pages."],
        ["Keyword match events", "Saved match events for active seed keywords during this run; this is not necessarily a unique-page count."],
        ["Retries scheduled", "Attempts placed back into retry work during this run."],
        ["Processing errors", "Errors after or around retrieval, such as unreadable or rejected content."],
    ], [2.15, 4.55])
    add_note(doc, "Run counters", "The eight cards reset for each run. They are not lifetime totals. A failed attempt is not the same as a permanently bad seed.")
    add_heading(doc, "Saved-progress line", 2)
    add_table(doc, ["Item", "Meaning and update timing"], [
        ["Seed URLs — processed of total", "Active root seeds that reached success or a final problem/dead-letter state, compared with the saved active total."],
        ["Successful", "Seed URLs whose saved health state is successful/healthy."],
        ["Needs attention", "Saved seed URLs currently requiring review, disabled handling, or human verification."],
        ["Discovered URLs", "Unique non-seed addresses saved across queue states. Several seeds finding the same address count once. Refreshed when the app opens and after a crawl stops."],
        ["Local files — processed of total", "Supported files processed from the selected local research folder compared with the saved eligible total."],
    ], [2.25, 4.45])
    add_heading(doc, "Processed types line", 2)
    add_para(doc, "Shows the successful source-type totals for the current run—for example HTML, PDF, IMAGE, DOCX, MHT, CSV, JSON, SQLITE, ACCESS, DBF, XLSX, ZIP, SQL, or AUDIO. “None yet” means the run has not successfully completed a recognized type.")
    add_heading(doc, "Current URL", 2)
    add_para(doc, "Displays the most recently started address. Several addresses may be processing at the same time, so this line is a current activity indicator, not a promise that only one URL is active.")
    add_heading(doc, "Activity tabs and color legend", 2)
    add_table(doc, ["Tab", "Shows"], [
        ["URL activity", "Starts, fetches, redirects, challenges, successes, failures, retries, skips, and URL-level processing."],
        ["Keyword matches", "Active keyword matches and the source where each match was found."],
        ["All activity", "The combined stream, including startup, storage, OCR, structured data, and errors."],
    ], [1.7, 5.0])
    add_table(doc, ["Legend color", "Typical meaning"], [
        ["Green", "Retrieved or completed"], ["Blue", "Fetching"], ["Teal", "Discovery"], ["Purple", "Keyword match"],
        ["Orange", "Retry"], ["Red", "Failed or dead-letter"], ["Brown", "Blocked"], ["Magenta", "CAPTCHA or human challenge"],
        ["Gray", "Skipped"], ["Dark red", "Processing error"],
    ], [1.7, 5.0])
    add_para(doc, "You may select visible log text and press Ctrl+C. The status line below the activity area reminds you which figures are run-only and which show saved progress.")

    add_section_break(doc)
    add_heading(doc, "4. How a crawl works", 1)
    add_heading(doc, "The work order", 2)
    add_steps(doc, [
        "History Crawler selects eligible active seed URLs that are due.",
        "It checks robots rules, host delays, limits, and saved health information.",
        "A successful page is processed and stored. Links on that page are normalized and eligible links are queued as discovered URLs.",
        "Discovered URLs are crawled from the queue as work becomes available; they do not wait for every seed to finish.",
        "Temporary failures may be retried. Final failures and challenges are retained with a reason so they can be reviewed.",
        "If local-file scanning and newspaper discovery are enabled, those sources add their own eligible work.",
        "When no eligible seed, discovered, retry, newspaper, or local-file work remains for the current run, active crawling becomes idle and the run can finish cleanly.",
    ])
    add_heading(doc, "Seeds are grouped and resumed", 2)
    add_para(doc, "The database remembers progress. A new run normally continues with due or unfinished seeds instead of blindly starting at row one. The InitialSeedLimit setting controls how many roots may be selected for a run; 0 means unlimited. Queue and concurrency limits still keep actual processing manageable.")
    add_heading(doc, "Restarting the seed starting point", 2)
    add_para(doc, "Settings > Application settings > Restart all seed URLs from the beginning resets only seed scheduling/progress. It does not delete captured pages, matches, documents, structured records, discovered URLs, or research results. The program previews the active count and asks “Are you sure?” first.")
    add_heading(doc, "When work runs out", 2)
    add_para(doc, "The crawler does not invent new research targets. If all eligible seed, discovered, retry, newspaper, and local work is exhausted, it finishes or remains idle according to the worker state. New or re-enabled seeds, a scheduled retry date, another local file, a refreshed newspaper search, or a manual restart creates future work.")
    add_heading(doc, "Permanent and temporary failures", 2)
    add_bullets(doc, [
        "404 Not Found and 410 Gone are evidence from that particular request. They are retained and counted; History Crawler does not automatically erase the seed.",
        "A 410 usually means the publisher says the resource is permanently gone, but the program still preserves the record for audit and user choice.",
        "Timeouts, 429 rate limits, 5xx server errors, network problems, and many challenges may be temporary and are handled under retry rules.",
        "A successful later crawl resets the current consecutive-failure streak.",
    ])
    add_heading(doc, "Domain-level challenge handling", 2)
    add_para(doc, "When a domain produces a recognized human challenge, related URLs on that domain can be paused and grouped as problem work instead of repeatedly failing. After a successful permitted manual verification, History Crawler retries the selected URL and other Turnstile-blocked URLs from the same website during the temporary verified session.")

    add_section_break(doc)
    add_heading(doc, "5. Search collection", 1)
    add_figure(doc, "collection-search.png", "Figure 2  Collection search with saved results and source text")
    add_heading(doc, "What search examines", 2)
    add_para(doc, "Search collection examines saved webpages, documents, OCR text, structured datasets, and completed audio transcripts. It searches the local SQL collection and does not contact OpenAI.")
    add_heading(doc, "Search controls", 2)
    add_table(doc, ["Control", "Use"], [
        ["Search box", "Type up to 200 characters."],
        ["Everything", "Show all saved source types."],
        ["Web pages", "Show saved crawled-page text."],
        ["Documents", "Show saved document and OCR text."],
        ["Audio transcripts", "Show completed transcripts."],
        ["Structured data", "Show imported datasets, tables, database rows, and archive entries."],
        ["Search", "Run the query. A prominent Searching… panel appears while the database works."],
        ["Search help", "Open novice examples for AND, OR, NOT, quotation marks, and parentheses."],
        ["Copy text", "Copy the selected saved text to the clipboard."],
        ["Open original", "Open an HTTP/HTTPS source or reveal a local source in File Explorer; unsafe schemes are rejected."],
        ["Optional AI tools…", "Open paid AI research choices only when deliberately selected."],
    ], [1.7, 5.0])
    add_heading(doc, "Ordinary words and Boolean logic", 2)
    add_figure(doc, "collection-search-help.png", "Figure 3  Search help with beginner examples for Boolean searches", 6.4)
    add_table(doc, ["Example", "Meaning"], [
        ["Lincoln railroad Illinois", "Broad simple search: finds sources containing any of the words; sources matching more words normally rank higher."],
        ["Lincoln AND railroad", "Requires both words."],
        ["Lincoln OR Douglas", "Accepts either word or both."],
        ["Lincoln NOT Nebraska", "Requires Lincoln and excludes Nebraska."],
        ["(Lincoln OR Douglas) AND debate", "Requires debate plus either Lincoln or Douglas."],
        ["\"Gettysburg Address\"", "Requires the exact phrase."],
    ], [2.6, 4.1])
    add_note(doc, "Important", "A space alone is a broad OR-style search in the current version, not an implied AND. Write AND when every word must be present. Boolean words may be uppercase or lowercase.", "warning")
    add_heading(doc, "Search results", 2)
    add_para(doc, "The left side lists matching sources. Select one to load its saved text on the right. The source line shows where it came from. Search reports the number of matching sources, explains invalid Boolean expressions, and suggests fewer or different words when nothing matches.")

    add_section_break(doc)
    add_heading(doc, "6. Every Settings menu choice", 1)
    add_figure(doc, "settings.png", "Figure 4  Application settings and the protected seed restart choice", 5.4)
    add_table(doc, ["Menu item", "What opens and what changes"], [
        ["Application settings…", "Editable existing configuration values and the seed-progress restart command."],
        ["Start a new research project…", "Safely clears the old research collection while always preserving every Seed URL. Seed Keywords may be kept or cleared."],
        ["First-time setup guide…", "The five friendly setup pages. Stop the crawler before changing setup choices."],
        ["Seed URLs…", "Editable root addresses and crawl instructions."],
        ["Problem seeds…", "Saved failures, challenges, disabled seeds, retries, verification, and cleanup."],
        ["Historical newspaper archives…", "Free Library of Congress searches, grouped state choices, result limit, and repeat interval."],
        ["Seed keywords…", "Words or phrases used to record matches and drive newspaper searches."],
        ["Prepare clean starter database…", "Creates a separate verified installer database. The working research database is not wiped."],
        ["OpenAI key…", "Adds, decrypts for editing, replaces, or removes the current user’s Windows-encrypted API key and offers feature choices."],
        ["Log files…", "Read-only log viewer with refresh and protected deletion."],
        ["Exit", "Closes History Crawler. Alt+F4 performs the same action."],
    ], [2.15, 4.55])
    add_heading(doc, "Application settings", 2)
    add_para(doc, "The grid edits only existing values; it cannot add, rename, or remove properties. OpenAI keys are managed separately. Changes save when the window closes, and the crawler must be restarted before new values take effect. Environment settings may override the file.")
    add_bullets(doc, [
        "Save and close: validates and saves the edited values.",
        "Close without saving: discards edits.",
        "Crawler : InitialSeedLimit: 0 means unlimited seed selection for the run.",
        "Restart all seed URLs from the beginning: preserves collected data but reschedules active roots.",
    ])
    add_heading(doc, "Start a new research project", 2)
    add_para(doc, "Choose Settings > Start a new research project… when you want to reuse History Crawler for a different research topic. Seed URLs are protected by the program and are never removed by this feature.")
    add_bullets(doc, [
        "Keep Seed Keywords: clears collected results and starts the same topic again with the same roots and keywords.",
        "Clear Seed Keywords: clears collected results and keywords so you can enter a different topic. Seed URLs still remain.",
        "Cleared information includes collected pages, discovered URLs, OCR and document text, transcripts, structured-data records, keyword matches, failures, retry/queue records, and other prior research results.",
        "Seed progress and automatic-failure state are reset. Automatically disabled seeds become eligible again; seeds you disabled manually remain disabled.",
        "Application settings, the Windows-encrypted OpenAI key, source files in your research folder, and disk log files are not removed.",
    ])
    add_note(doc, "Verified protection", "The crawler must be stopped. History Crawler previews the exact tables, labels Seed URLs as Protected, creates and verifies a safety backup, requires an acknowledgement and the words START NEW, and asks for final confirmation. The operation is rolled back if any verification fails.", "warning")

    add_section_break(doc)
    add_heading(doc, "7. Seed URLs and Seed Keywords", 1)
    add_heading(doc, "Seed URLs", 2)
    add_figure(doc, "seeds.png", "Figure 5  The Seed URL editor keeps changes staged until Save changes", 6.7)
    add_para(doc, "A Seed URL is a root HTTP or HTTPS address that History Crawler may revisit. The editor loads the current rows and stages edits until Save changes.")
    add_table(doc, ["Column", "Purpose"], [
        ["URL", "Absolute HTTP or HTTPS address. Embedded usernames/passwords are rejected."],
        ["Domain", "Host grouping used for polite scheduling and problem handling."],
        ["Category", "Optional research classification."],
        ["Priority", "Relative scheduling importance."],
        ["Max Depth", "How far from this root discovered links may be followed; zero or greater."],
        ["Follow External", "Whether links to other domains may be followed from this root."],
        ["Is Active", "Cleared means the seed remains saved but is skipped."],
        ["Notes / Source Type / Discovery Source / Tags", "Provenance and organizational notes."],
        ["Created At / Last Crawled At", "Read-only history timestamps."],
    ], [2.1, 4.6])
    add_heading(doc, "Seed editor buttons", 2)
    add_bullets(doc, [
        "Add creates a staged active row. Replace the example value before saving.",
        "Delete selected stages a deletion. For URLs, existing crawl history is retained; database references may prevent deletion.",
        "Save changes validates every row and commits the batch. A failed batch rolls back.",
        "Reload discards staged edits only after confirmation.",
    ])
    add_heading(doc, "Seed Keywords", 2)
    add_figure(doc, "keywords.png", "Figure 6  Seed Keywords identify the people places events and phrases that matter to the research", 5.8)
    add_para(doc, "Seed Keywords are active research words or phrases. They produce keyword-match events and are submitted to the enabled free historical-newspaper provider. The same Add, Delete selected, Save changes, and Reload workflow applies. Deleting a keyword also removes its recorded keyword-hit rows.")

    add_section_break(doc)
    add_heading(doc, "8. Problem seeds and human verification", 1)
    add_figure(doc, "problem-seeds-loading.png", "Figure 7  A prominent loading message appears while the problem list is being prepared", 6.7)
    add_heading(doc, "What is retained", 2)
    add_para(doc, "Challenge failures, not-found responses, dead-letter outcomes, retry dates, failure counts, first and last failure dates, reasons, and active/disabled status are stored in the database—not just in the current screen session. Successful later access can reset the current failure streak.")
    add_heading(doc, "Problem Seeds buttons", 2)
    add_table(doc, ["Button", "Result"], [
        ["Verify and crawl", "Opens a separate app-controlled Edge verification window. After a permitted human check succeeds, the session is saved temporarily and related blocked URLs on the same site are prepared for retry."],
        ["Retry next crawl", "Schedules the selected URL for the next crawler run. Human-verification entries remain visible until verification is completed."],
        ["Disable", "Keeps the seed and history but prevents future crawling until it is re-enabled."],
        ["Delete permanently", "Deletes the seed URL after confirmation. Previously collected pages remain."],
        ["Clean up…", "Bulk preview and then disable or permanently delete matching seeds."],
        ["Refresh", "Reloads the saved problem set. A prominent loading panel appears."],
        ["Close", "Leaves the window without an automatic cleanup."],
    ], [1.7, 5.0])
    add_heading(doc, "Clean up problem seeds", 2)
    add_para(doc, "Choose Disable matching seeds or Delete matching seeds permanently, then choose one rule: consecutive failed attempts at least (spinner defaults to 5) or first failed attempt on or before a date. Choose Preview matches, review and sort the exact rows, then Apply to previewed seeds. If a seed recovers or changes after preview, the guarded update safely skips it.")
    add_heading(doc, "Sorting", 2)
    add_para(doc, "Click any column heading to sort; click it again to reverse the order. The cleanup preview and main Problem Seeds grid both support column sorting.")
    add_heading(doc, "Human validation: what it can and cannot do", 2)
    add_bullets(doc, [
        "You may click a legitimate site’s verification box or enter a code in the app-controlled browser when the site allows it.",
        "The saved session is Windows-encrypted, temporary, limited to the crawler, and deleted when the crawler process exits.",
        "The feature does not solve or bypass validation automatically and does not guarantee access.",
        "It does not reuse a separately opened personal Edge window. The verified session must occur in the window opened by History Crawler.",
        "For subscription sites, account access and terms still govern what you may view or collect.",
    ])

    add_section_break(doc)
    add_heading(doc, "9. Historical newspaper archives", 1)
    add_para(doc, "The historical-newspaper feature uses the free Library of Congress Chronicling America service. No account or password is required. Active Seed Keywords are submitted, OCR matches are saved, and matching newspaper-page addresses are queued for normal processing.")
    add_heading(doc, "Controls", 2)
    add_bullets(doc, [
        "Search public historical newspaper archives turns the feature on or off.",
        "Select all states and Clear all affect the full list.",
        "States are grouped Southwest, Northwest, Midwest, Southeast, and Northeast, in that order.",
        "Each region has its own Select all and Clear all buttons.",
        "Maximum matches per keyword accepts 1 through 160.",
        "Repeat searches after days accepts 1 through 365.",
        "Save and close validates and saves; Cancel leaves settings unchanged.",
    ])
    add_note(doc, "Removed subscription automation", "Newspapers.com and Ancestry login automation are not part of the current program. Their protected login/Cloudflare sessions could not be shared reliably with the crawler, and History Crawler does not store those website passwords.", "warning")

    add_section_break(doc)
    add_heading(doc, "10. Files History Crawler can process", 1)
    add_para(doc, "Support means the program can detect the type, apply the configured safety limits, extract searchable text or records when possible, and retain the source address/path and processing provenance. A corrupt, encrypted, unsupported variant, oversized file, or image with no readable text can still be rejected.")
    add_table(doc, ["Category", "Extensions or source types", "How it is handled"], [
        ["Web pages", "HTML, HTM and ordinary web responses", "Parses readable text, title, links, metadata, and structured page data."],
        ["Saved web archives", "MHT, MHTML", "Decodes the MIME archive and extracts archived HTML/text. Embedded scripts are not executed."],
        ["Documents", "PDF, DOCX, TXT, MD", "Extracts digital text; PDF can fall back to page OCR. Word packages are read as document content."],
        ["Images", "JPG, JPEG, PNG, BMP, TIF, TIFF, WEBP", "Validates size and dimensions, then uses Tesseract OCR when available."],
        ["Delimited data", "CSV, TSV, TAB", "Reads bounded rows, columns, cells, and headers into searchable records."],
        ["JSON data", "JSON, JSONL, NDJSON, GeoJSON", "Flattens bounded values and records with source provenance."],
        ["XML data", "XML", "Reads with DTDs prohibited and external resolution disabled."],
        ["Spreadsheets", "XLSX, XLS", "Reads worksheets and bounded cell data; legacy XLS requires the 64-bit Access Database Engine."],
        ["Databases", "SQLite, SQLite3, DB, MDB, ACCDB, DBF", "Reads tables/records in read-only mode. Access files require the 64-bit Microsoft database engine."],
        ["SQL text", "SQL", "Indexes statements as plain text only. Nothing in the file is executed."],
        ["Bulk archives", "ZIP", "Reads only supported entries under entry, expansion, nesting, size, and compression-ratio limits."],
        ["Repository/bulk endpoints", "API, export, bulk, dataset, repository and download links", "Recognizes likely data exports and prioritizes direct datasets over fragile screen scraping."],
        ["Audio (optional)", "MP3, MP4, MPEG, MPGA, M4A, WAV, WEBM, FLAC, OGG, AAC, WMA, AIFF", "When enabled, converts as needed with FFmpeg and sends bounded chunks for paid OpenAI transcription."],
    ], [1.25, 2.0, 3.45])
    add_heading(doc, "PDF and image processing", 2)
    add_steps(doc, [
        "Validate file size, PDF page count, image dimensions, and pixel count.",
        "Try direct digital-text extraction first for PDF pages.",
        "For pages below the minimum readable-character threshold, render the page and use OCR when the optional tools are present.",
        "Combine digital and OCR page text without exceeding the extracted-character limit.",
        "Save warnings and provenance. An image with no recognizable letters reports “OCR found no readable text in this image”; that is a content result, not evidence of a virus.",
    ])
    add_heading(doc, "Structured data and database files", 2)
    add_para(doc, "Each supported dataset is categorized, read under row/table/column/cell/character limits, converted into searchable text, and saved with its original source plus format, record/table/entry counts, and truncation warnings. SQLite opens query-only with trusted schema disabled. Access, legacy Excel, and DBF are read through the installed data provider. ZIP contents remain bounded and are not unpacked into arbitrary user folders.")
    add_note(doc, "100 MB choice", "The local-file and PDF limits may be configured around 100 MB when your computer has sufficient memory and the sources justify it. The separate structured-data download default is more conservative. Larger limits increase processing time, memory use, disk use, and exposure to malformed files; raise them deliberately rather than removing them.", "warning")

    add_section_break(doc)
    add_heading(doc, "11. Optional AI tools", 1)
    add_heading(doc, "Adding or removing a key", 2)
    add_para(doc, "Settings > OpenAI key shows the encrypted value, not the plaintext key. Choose Decrypt and edit to view or change it. When a new key is saved, History Crawler asks which optional features to enable. Recommended: manual AI tools only. Automatic transcription and semantic indexing remain off unless selected. When the key is deleted, the program asks whether to turn all paid AI features off; saved results and transcripts remain.")
    add_heading(doc, "Feature choices", 2)
    add_table(doc, ["Feature", "Behavior"], [
        ["Manual AI research and handwriting", "Runs only after you choose a task and confirm source material may be sent to OpenAI."],
        ["Automatic audio transcription", "May make paid API requests while crawling eligible audio. Off by default."],
        ["AI embedding index", "May make paid requests while building semantic vectors. Off by default."],
        ["Ordinary crawl and collection search", "No OpenAI key or OpenAI credits required."],
    ], [2.2, 4.5])
    add_heading(doc, "AI research workspace", 2)
    add_para(doc, "The optional workspace can search saved source material, let you select evidence, ask a question, request a source summary, identify explicitly supported people/places/dates/organizations/events/relationships, assist with a handwriting image, and save results with citations. Evidence validation requires findings and quotations to correspond to selected source text.")
    add_note(doc, "Billing", "ChatGPT/Codex subscriptions and OpenAI API billing are separate. History Crawler cannot read a live prepaid balance from a normal API key. Use the links in About to review API balance, usage, limits, and alerts.", "warning")

    add_section_break(doc)
    add_heading(doc, "12. What is saved, returned, and logged", 1)
    add_heading(doc, "Saved research information", 2)
    add_table(doc, ["Saved item", "Examples"], [
        ["Source identity", "Original URL or local path, final redirected URL, domain, source type, discovery source, and tags."],
        ["Retrieval evidence", "HTTP status, retrieval time, title, raw/processed content where configured, hashes, redirects, and conditional-request state."],
        ["Readable content", "Page text, document text, OCR text, transcript text, or normalized structured-data text."],
        ["Research signals", "Seed keyword hits, match counts, snippets, discovered links, and JSON-LD/metadata."],
        ["Structured provenance", "Format, table/sheet/entry name, row or record numbers, counts, warnings, and source address."],
        ["Health and retries", "Status, failure reason, consecutive count, first/last failure, next check, challenge type, and active state."],
        ["Optional AI outputs", "Questions, summaries, findings, evidence quotations, citations, and completed transcripts."],
    ], [2.0, 4.7])
    add_heading(doc, "Log files", 2)
    add_para(doc, "Settings > Log files opens a read-only viewer. Choose a file, Refresh to load new entries, or Delete selected log after stopping the crawler and confirming. Very large logs show the last 2 MiB to keep the screen responsive. Daily log retention is limited by configuration; the current setup retains a rolling set rather than every log forever.")
    add_heading(doc, "Common log messages", 2)
    add_table(doc, ["Message", "Interpretation"], [
        ["START / HTTP / RETRIEVED", "A URL began, returned a response, and was accepted."],
        ["REDIRECT", "The server sent the request to another address; both are recorded."],
        ["DISCOVERY", "A new eligible link was saved or queued."],
        ["CHALLENGE", "A recognized human/anti-bot check blocked automated access."],
        ["FAILURE DEAD-LETTER", "No automatic retries remain for this work item; it is retained for review."],
        ["OCR found no readable text", "OCR ran, but no usable characters were recognized. The image may contain no text, be too poor, or use unsupported writing."],
        ["SKIPPED / BLOCKED", "A policy, robots rule, limit, unsupported type, or safety check prevented processing."],
        ["PROCESSING ERROR", "Retrieval may have succeeded, but content conversion/storage failed."],
    ], [2.15, 4.55])

    add_section_break(doc)
    add_heading(doc, "13. Donations subscriptions suggestions and saved responses", 1)
    add_para(doc, "The Support menu provides optional ways to support the project, request occasional news, send an improvement idea, and save received subscription or suggestion emails in the local History Crawler database. These choices do not affect crawling or search results.")
    add_heading(doc, "Donations", 2)
    add_figure(doc, "support-donation.png", "Figure 8  The donation page opens the configured PayPal contribution page and explains what History Crawler stores", 5.5)
    add_bullets(doc, [
        "Choose Support > Donate or choose Support History Crawler on the About page.",
        "Donate securely with PayPal opens an HTTPS page on paypal.com in the normal browser.",
        "Copy PayPal contribution link copies the same approved PayPal address for pasting elsewhere.",
        "PayPal handles the payment and keeps the payment history and receipt. History Crawler does not receive or store card, bank, or PayPal sign-in information.",
        "Contributions are optional, are not tax-deductible charitable donations, and do not buy features or guaranteed support.",
    ])
    add_heading(doc, "Subscribe for updates", 2)
    add_figure(doc, "support-subscribe.png", "Figure 9  The subscription page prepares an email only after the required information and consent are supplied", 5.6)
    add_para(doc, "Enter a name, email address, type of historical research, and optional organization or comments. Select the consent box. Prepare email opens the computer's default email program, Open Gmail opens a Gmail compose page, and Copy email copies the complete message. Review the prepared email and press Send yourself; History Crawler does not send it silently.")
    add_heading(doc, "Send a suggestion", 2)
    add_figure(doc, "support-suggestion.png", "Figure 10  The suggestion page asks for enough context to understand and categorize the request", 5.6)
    add_para(doc, "Enter contact information, the type of research, a category, and the suggestion. The contact checkbox is optional. Do not include passwords, financial information, confidential unpublished sources, or sensitive personal information. As with subscriptions, the app only prepares or copies the email; you decide whether to send it.")
    add_heading(doc, "Save received responses", 2)
    add_figure(doc, "support-received-responses.png", "Figure 11  Received responses can be imported from the clipboard and sorted by any column", 6.7)
    add_steps(doc, [
        "In Gmail, open a History Crawler subscription or suggestion email.",
        "Select and copy the complete message, including the structured History Crawler section.",
        "In History Crawler, choose Support > Received responses.",
        "Choose Import subscription from clipboard or Import suggestion from clipboard.",
        "Wait for the saved confirmation. The app validates the message, rejects the wrong type, and ignores an exact duplicate.",
        "Use Refresh to reload the local list. Click a column heading to sort the saved responses.",
    ])
    add_note(doc, "Email privacy", "Subscription and suggestion emails are sent to HistoryCrawlerApp@gmail.com only after the user presses Send. Imported copies are stored in the local History Crawler database. Donation records remain in PayPal.", "warning")

    add_section_break(doc)
    add_heading(doc, "14. Security and privacy", 1)
    add_para(doc, "No crawler can guarantee that hostile content will never be encountered. History Crawler reduces risk by treating remote material as untrusted data, refusing dangerous execution paths, applying limits before expensive processing, and keeping sensitive settings outside the project. Continue using Windows Security and current software.")
    add_table(doc, ["Protection", "What it does"], [
        ["Safe URL policy", "Accepts HTTP/HTTPS for web sources; rejects credential-bearing and unsafe custom schemes. Private-network targets are blocked by default."],
        ["Robots and rate limits", "Fails closed when robots policy cannot be established, caches decisions, applies host delays, and limits per-host concurrency."],
        ["Redirect checks", "Rechecks redirect destinations instead of trusting the original URL alone."],
        ["Download/content limits", "Caps response sizes, local files, images, PDFs, structured datasets, extracted characters, rows, tables, columns, and cells."],
        ["Image/PDF validation", "Checks bytes, dimensions, pixel count, page count, rendering timeout, and extracted text size before or during OCR."],
        ["ZIP-bomb protection", "Limits archive entries, expanded bytes, nesting depth, entry size, and compression ratio; reads supported entries only."],
        ["Database read-only modes", "SQLite uses read-only/query-only mode with trusted schema disabled; Access/Excel sources are opened for data reading."],
        ["XML protection", "Prohibits DTD processing and external entity resolution."],
        ["No SQL execution", "SQL downloads are searchable text only. Collected statements are never run."],
        ["External-tool trust", "Tesseract, ImageMagick, Ghostscript, and FFmpeg require explicit absolute trusted paths. Arguments are passed without a shell, timeouts are enforced, and hung process trees are stopped."],
        ["No automatic file launch", "Local collected files are revealed in File Explorer rather than executed. Only approved web schemes may open in a browser."],
        ["Secret protection", "The OpenAI key is Windows-encrypted for the current user in the private local-data folder, outside settings and Git. Release building rejects apparent embedded keys."],
        ["Temporary verification session", "Human-verification session data is Windows-encrypted for the crawler and deleted when the crawler exits."],
        ["Community response validation", "Subscription and suggestion emails are parsed only from the structured History Crawler section, checked for required fields and size limits, and deduplicated before local storage. Nothing in an email is executed."],
        ["Transactional changes", "Seed edits and cleanup use confirmation and transactions. Starting a new project also creates and verifies a safety backup, protects every Seed URL, restores constraints and triggers, verifies the cleared tables, and rolls back on failure."],
        ["Provenance and deduplication", "Source, timestamps, hashes, redirects, and structured record origins help identify what was collected and avoid needless duplicate work."],
    ], [2.15, 4.55])
    add_heading(doc, "Safe operating habits", 2)
    add_bullets(doc, [
        "Install tools only from their official publishers and keep them updated.",
        "Do not disable Windows Security to make a crawl succeed.",
        "Keep database backups before large migrations and release preparation. The Start a new research project feature creates and verifies its own safety backup automatically.",
        "Do not raise file, archive, page, or timeout limits without a research reason.",
        "Review website terms and permissions, especially for subscriptions, personal data, and restricted collections.",
        "Never distribute the private safety backup, API key, personal logs, or working research database with an installer.",
    ])

    add_section_break(doc)
    add_heading(doc, "15. Clean starter database and release preparation", 1)
    add_heading(doc, "Why this feature exists", 2)
    add_para(doc, "A new user should receive useful Seed URLs and Seed Keywords, not your private crawl results, OCR text, transcripts, failures, logs, AI findings, or research history. Prepare clean starter database creates a separate installer copy for that purpose.")
    add_heading(doc, "What the tool does", 2)
    add_steps(doc, [
        "Stops if the crawler is running.",
        "Loads successful HTTP/HTTPS addresses that are not already seeds.",
        "Selects stable, query-free addresses by default; login, account, session, search, calendar, sorting, paging, and query-string addresses require review.",
        "Lets you select recommended, select all, clear all, or individually check candidates.",
        "Creates a private COPY_ONLY safety backup of the working database in the current user’s local History Crawler backup folder.",
        "Restores that backup into the fixed separate HistoryCrawlerStarterDB database.",
        "Adds only approved successful addresses to the starter Seed URLs.",
        "Clears every non-seed table in the separate copy, preserves Seed URLs, Seed Keywords, and schema version, and resets seed progress/failures.",
        "Verifies the clean logical state, exports HistoryCrawlerStarterSeeds.json, creates HistoryCrawlerStarter.bak with checksums, and runs RESTORE VERIFYONLY.",
        "Reports the clean files and the separate private recovery backup. The working research database is not wiped or renamed.",
    ])
    add_heading(doc, "Release builder", 2)
    add_para(doc, "The release builder publishes self-contained Windows x64 worker and desktop files, adds the clean starter backup and current Documentation folder, checks published settings for apparent API keys, adds a README FIRST file, and creates HistoryCrawler-win-x64.zip inside the solution’s Installer artifacts folder. Microsoft SQL Server LocalDB remains a prerequisite.")
    add_note(doc, "Never ship", "Do not include the private safety backup stored under the current user’s local History Crawler Backups folder. Ship only the verified HistoryCrawlerStarter.bak and its seed JSON export.", "warning")

    add_section_break(doc)
    add_heading(doc, "16. Troubleshooting", 1)
    add_table(doc, ["Symptom", "What to do"], [
        ["Crawler can’t start", "Read the visible All activity output and Settings > Log files. Confirm SQL LocalDB is running, the connection points to the expected database, required files exist, and no older worker is still running."],
        ["Status badge/button did not change", "Wait for the startup signal or worker exit. If it remains inconsistent, stop the worker, close History Crawler, reopen it, and inspect the newest log."],
        ["Problem Seeds timeout", "No records are changed on the handled timeout. Close long SQL queries, wait, choose Refresh, and confirm SQL Server is responsive."],
        ["Retry seems frozen", "The prominent loading panel should remain visible while the database schedules and reloads. A very large problem set can still take time; check SQL activity and the log rather than clicking repeatedly."],
        ["Seed processed count does not move", "The current work may be discovered URLs, retries, local files, or a long seed. The saved seed line updates at defined refresh points, not for every screen repaint."],
        ["OCR warning on an image", "Open the image and check whether it truly contains readable text. Improve the scan, install/verify Tesseract, try image preprocessing, or accept that the source has no extractable text."],
        ["PDF text is poor", "Confirm Ghostscript, ImageMagick, and Tesseract paths; check the PDF/page limits; try a clearer scan; review the processing log."],
        ["Access or old Excel file will not read", "Install the 64-bit Microsoft Access Database Engine and restart History Crawler. The provider architecture must match the 64-bit app."],
        ["Cloudflare verification repeats", "Use Verify and crawl in the app-opened window, not a separate Edge window. If the site still refuses, leave it retained/disabled and do not attempt a bypass."],
        ["No search results", "Try fewer words, a broader OR search, a quoted spelling variation, or Everything. Confirm the source was successfully processed and saved."],
        ["OpenAI feature fails", "Confirm the encrypted key can be decrypted by this Windows user, the desired feature is enabled, API billing/limits are valid, and ordinary local search works separately."],
        ["Starter package refused", "Stop the crawler, ensure the output folder is writable, decide whether to replace an existing starter copy, review candidates, check acknowledgement, and type CREATE STARTER exactly."],
    ], [2.05, 4.65])

    add_section_break(doc)
    add_heading(doc, "17. Keyboard and mouse reference", 1)
    add_table(doc, ["Key or action", "Where", "Result"], [
        ["Enter", "Collection search box", "Runs the current search."],
        ["Enter", "Optional AI feature choices", "Activates Save choices when it is the default button."],
        ["Escape", "Windows with a Cancel/Close (Esc) button", "Closes the window unless a protected operation is actively running."],
        ["Alt+F4", "Main window", "Requests Exit."],
        ["Tab / Shift+Tab", "Most windows", "Moves forward/backward among controls."],
        ["Space", "Checkbox or focused button", "Toggles the checkbox or activates the button."],
        ["Arrow keys", "Lists, grids, combo boxes", "Moves the selection."],
        ["Up / Down", "Failure-count spinner", "Raises or lowers the number by one."],
        ["Mouse wheel", "Failure-count spinner", "Raises or lowers the number by one."],
        ["Click column heading", "Problem/cleanup grids", "Sorts; click again to reverse."],
        ["Ctrl+C", "Selected log or saved text", "Copies the selection."],
        ["Ctrl+A", "Editable text field", "Selects all text in the field."],
        ["F4 or Alt+Down", "Standard Windows combo/date controls", "Opens the available list or calendar when supported by Windows."],
    ], [1.45, 2.2, 3.05])
    add_note(doc, "Avoid repeated clicks", "When a Searching…, Loading…, Saving…, Updating…, or Preparing… panel is visible, wait for it to finish. Repeated clicks do not make database work faster.")

    doc.add_page_break()
    add_heading(doc, "Appendix A. How to…", 1)
    howtos = [
        ("Start a normal crawl", ["Open History Crawler.", "Confirm the badge says Crawler stopped.", "Review Seed URLs and Seed Keywords if needed.", "Choose Start crawler.", "Watch the badge, counters, current URL, and URL activity tab."]),
        ("Stop safely", ["Choose Stop once.", "Wait while the badge says Stopping….", "Continue only after it reports Crawler stopped.", "Then close the app or change database-sensitive settings."]),
        ("Search for Lincoln–Douglas debates", ["Choose Search collection….", "Type (Lincoln OR Douglas) AND debate.", "Leave Everything selected or choose a source type.", "Press Enter or choose Search.", "Select a result, read the saved text, then Copy text or Open original."]),
        ("Require every search idea", ["Write AND explicitly—for example Lincoln AND Douglas AND debate.", "Do not rely on spaces; a simple space-separated search is broad and accepts any term.", "Use quotation marks for an exact phrase and parentheses around OR choices."]),
        ("Add a seed URL", ["Choose Settings > Seed URLs….", "Choose Add.", "Replace the example URL with a complete http:// or https:// address.", "Set Max Depth, Follow External, Active, category, and notes as desired.", "Choose Save changes, then restart the crawler."]),
        ("Start all seeds from the beginning without deleting research", ["Stop the crawler.", "Choose Settings > Application settings….", "Choose Restart all seed URLs from the beginning.", "Read the active-seed preview and choose Yes only if correct.", "Close Settings and start the next crawl."]),
        ("Start a new research project without losing Seed URLs", ["Stop the crawler.", "Choose Settings > Start a new research project….", "Choose whether to keep or clear Seed Keywords.", "Review the table preview; Seed URLs must say Protected.", "Select the acknowledgement and type START NEW.", "Choose Start new research project, then confirm one final time.", "Read the completion message and note the verified backup location.", "If you cleared the keywords, enter the new Seed Keywords before starting the crawler."]),
        ("Retry a failed source", ["Stop the crawler if you are changing problem data.", "Choose Settings > Problem seeds….", "Wait for Loading problem seeds… to finish.", "Select a row and choose Retry next crawl.", "Start the crawler when ready."]),
        ("Disable a dead seed but keep history", ["Open Problem seeds and select the row.", "Choose Disable and confirm.", "The seed stays in the database and previously captured pages remain."]),
        ("Bulk-disable old failures", ["Open Problem seeds > Clean up….", "Choose Disable matching seeds.", "Choose the failure-count rule or date rule.", "Choose Preview matches.", "Sort and review the exact rows.", "Choose Apply to previewed seeds and confirm."]),
        ("Handle a legitimate human verification", ["Open Problem seeds and select the challenge row.", "Choose Verify and crawl.", "Complete the permitted check in the window History Crawler opened.", "After verification is saved, allow the crawler to retry this and related blocked URLs on that domain.", "If the site refuses, stop; do not try to bypass it."]),
        ("Include local files", ["Choose Settings > First-time setup guide or Application settings.", "Select a research folder you control.", "Enable local-file scanning.", "Confirm size limits and optional OCR tools.", "Start the crawler and watch Local files and Processed types."]),
        ("Read an MHT/MHTML archive", ["Place the file in the enabled research folder.", "Confirm .mht and .mhtml remain in LocalCrawler Extensions.", "Start the crawler.", "Search the collection for text from the archive; scripts and embedded executables are not run."]),
        ("Read a database file", ["Place the supported file in the research folder or crawl a direct download you are permitted to retrieve.", "For MDB/ACCDB/XLS, install the 64-bit Microsoft Access Database Engine.", "Start the crawler.", "History Crawler reads bounded records in safe/read-only mode and saves searchable text with table/record provenance.", "Search under Structured data."]),
        ("Improve a scanned PDF", ["Install and verify Tesseract, ImageMagick, and Ghostscript in trusted paths.", "Keep PDF/image limits appropriate for the computer.", "Start the crawler.", "The program tries digital text first and OCRs weak pages.", "Review OCR warnings and the saved text before citing it."]),
        ("Enable optional AI without surprise costs", ["Choose Settings > OpenAI key….", "Enter the key and close to save it encrypted.", "Select only Manual AI research and handwriting.", "Leave automatic audio and indexing off until deliberately needed.", "Use About to open API usage, billing, limits, and alert pages."]),
        ("Make an optional PayPal contribution", ["Choose Support > Donate or open About and choose Support History Crawler.", "Read the payment and privacy explanation.", "Choose Donate securely with PayPal.", "Complete the payment only on the paypal.com page in your browser.", "Keep the receipt in PayPal; History Crawler does not store payment information."]),
        ("Subscribe for History Crawler news", ["Choose Support > Subscribe for updates.", "Enter the required name, email address, and research type.", "Select the consent box.", "Choose Prepare email, Open Gmail, or Copy email.", "Review the message and press Send yourself."]),
        ("Send a suggestion", ["Choose Support > Send a suggestion.", "Enter the required contact and research information.", "Choose a suggestion category and describe the improvement.", "Choose Prepare email, Open Gmail, or Copy email.", "Remove any private research details, review the message, and press Send."]),
        ("Save a received subscription or suggestion", ["Open the complete response email in Gmail and copy the whole message.", "Choose Support > Received responses.", "Choose the matching Import from clipboard button.", "Read the saved or duplicate message.", "Click a column heading to sort the local list."]),
        ("Prepare a clean release database", ["Stop the crawler and make sure SQL LocalDB is available.", "Choose Settings > Prepare clean starter database….", "Wait for the candidate preview.", "Review recommended and excluded addresses; do not include login/search/session URLs without a reason.", "Choose the output folder, acknowledge the clean contents, type CREATE STARTER, and create.", "Keep the private safety backup private; use the verified .bak and seed JSON with the release builder."]),
        ("Diagnose a startup failure", ["Read All activity for the first error.", "Choose Settings > Log files and open the newest log.", "Confirm SQL LocalDB, the configured database, and file paths.", "Close any old crawler worker.", "Reopen History Crawler and try once more; retain the exact error if help is needed."]),
    ]
    howto_page_starts = {2, 4, 6, 8, 10, 12, 14, 16, 18, 20}
    for howto_index, (title, steps) in enumerate(howtos):
        if howto_index in howto_page_starts:
            doc.add_page_break()
        add_howto(doc, title, steps)

    doc.add_page_break()
    add_heading(doc, "Appendix B. Current changes and removed features", 1)
    add_heading(doc, "Added or improved", 2)
    add_bullets(doc, [
        "First-time setup guide with AI off, optional local scanning, system checks, and safe defaults.",
        "Accurate running/stopped badge and startup signaling; clearer loading/searching messages.",
        "Saved seed progress, success/attention totals, discovered URL total, local-file progress, and processed-type counts.",
        "Boolean collection search with novice help and exact source-type filters.",
        "Problem-seed sorting, loading feedback, retry feedback, bulk preview, failure-count/date cleanup, and human-verification grouping.",
        "Free regional Library of Congress newspaper discovery with select/clear controls per region.",
        "Expanded PDF/image OCR, MHT, audio, structured data, database, spreadsheet, ZIP, repository export, and provenance support.",
        "Separate clean starter-database preparation, clean database installation on first run, release packaging, and API-key scanning.",
        "Protected Start a new research project workflow with an automatic verified backup, optional keyword clearing, full result cleanup, post-reset checks, and Seed URLs that are never removed.",
        "OpenAI key moved out of settings into Windows-encrypted per-user storage; adding/removing a key prompts for feature settings.",
        "Support pages for optional PayPal contributions, update subscriptions, suggestions, and validated clipboard import into local response tables.",
    ])
    add_heading(doc, "Removed or intentionally not included", 2)
    add_bullets(doc, [
        "Newspapers.com and Ancestry automated login/crawling.",
        "Website username/password storage or automatic form filling.",
        "Attaching to an unrelated personal Edge session.",
        "Automatic bypass of Cloudflare, CAPTCHA, paywalls, or human validation.",
        "Executing SQL downloads, macros, installers, or collected local files.",
        "AI enabled by default or automatic paid transcription/indexing without an explicit choice.",
    ])
    add_para(doc, "End of manual.", italic=True)

    path = OUT / "History_Crawler_User_Manual.docx"
    doc.save(path)
    return path


def build_brochure() -> Path:
    doc = Document()
    configure_document(doc, "History Crawler · Research Brochure")
    set_core_properties(doc, "History Crawler Research Brochure", "Plain language research feature and cost brochure")
    add_cover(
        doc,
        "History Crawler",
        "Build and search a collection from your own files and public web sources",
        "Free Windows research application · November 2026",
        "For historians, genealogists, students, archives, local history groups, and independent researchers",
        "History Crawler · Research Brochure",
        "Copyright Tom Affholter, 2026",
    )

    add_heading(doc, "Why historical research needs a crawler", 1)
    add_para(doc, "Historical research often grows into thousands of webpages, scanned letters, photographs, PDFs, spreadsheets, databases, newspaper pages, recordings, and notes. A useful name or date may be buried in a file saved years ago or on a website the researcher no longer remembers visiting.")
    add_heading(doc, "What History Crawler does", 2)
    add_para(doc, "A crawler starts with approved website addresses, reads eligible pages, saves useful text, and follows permitted links to related pages. History Crawler also reads supported files in a folder you choose. It places web and local material into one searchable collection while keeping the original URL or file path with each result.")
    add_bullets(doc, [
        "Collect from public websites and your own research folders.",
        "Read ordinary text and use OCR to look for printed words in scans.",
        "Search webpages, documents, data records, OCR text, and transcripts together.",
        "Stop safely and continue later without starting over.",
        "Keep failed or blocked sources for later review instead of stopping the whole crawl.",
    ])
    add_heading(doc, "Useful without an AI account", 2)
    add_para(doc, "The crawler, local-file reading, OCR, keyword matching, historical newspaper search, and ordinary collection search work without OpenAI. AI tools are optional, begin turned off, and run only after the researcher supplies an API key and enables a feature.")
    add_para(doc, "History Crawler gathers and organizes evidence. The researcher still decides whether a source is reliable, what it means, and whether it should be quoted.", bold_prefix="History Crawler gathers and organizes evidence.")

    doc.add_page_break()
    add_heading(doc, "Start with your research topic", 1)
    add_heading(doc, "Add Seed Keywords", 2)
    add_para(doc, "Seed Keywords are the names, places, organizations, dates, events, and phrases that describe the project. History Crawler records matches in web pages, local files, OCR text, data records, newspaper results, and completed transcripts.")
    add_table(doc, ["Research project", "Possible Seed Keywords"], [
        ["Lincoln and the 1858 debates", "Abraham Lincoln, Stephen Douglas, Ottawa debate, Freeport Doctrine, 1858"],
        ["A family in Colorado", "surnames, town, county, mine, railroad, school, cemetery"],
        ["A local institution", "official name, former names, founders, address, neighborhood, dates"],
    ], [2.25, 4.45])
    add_heading(doc, "Use the starter Seed URLs", 2)
    add_para(doc, "The starter database contains about 80,000 Seed URLs from archives, libraries, museums, government collections, universities, and historical societies. You may add a source, correct an address, disable a source, or remove one you do not want. Seed URL changes are saved only after you choose Save changes.")
    add_heading(doc, "Let the collection grow", 2)
    add_para(doc, "As a seed is read, History Crawler finds eligible links and places them in the queue according to the seed's depth, domain, and external-link settings. Discovered URLs are counted and searched with the rest of the collection. They do not silently replace the permanent Seed URL catalog.")
    add_heading(doc, "Include your local research", 2)
    add_para(doc, "Choose a folder containing your research. History Crawler counts supported files, reads them within safety limits, adds searchable text to the same collection, and keeps the original path with the result. Your original files stay in place and are not renamed or deleted.")

    doc.add_page_break()
    add_heading(doc, "Files History Crawler can read", 1)
    add_para(doc, "The table gives common examples. You do not need to understand the file extensions before using the program.")
    add_table(doc, ["What you may have", "Examples", "What History Crawler does"], [
        ["Articles and documents", "PDF, Word DOCX, TXT, Markdown", "Reads normal text. A scanned PDF can be examined with OCR."],
        ["Pictures and scans", "JPG, JPEG, PNG, BMP, TIFF, WEBP", "Looks for readable printed text and reports when none is found."],
        ["Saved web pages", "HTML, HTM, MHT, MHTML", "Reads saved page text, titles, links, and basic source information."],
        ["Spreadsheets", "CSV, TSV, TAB, XLSX, older XLS", "Reads headings, worksheets, rows, columns, and cell values within safety limits."],
        ["Research data", "JSON, JSONL, NDJSON, GeoJSON, XML, DBF", "Turns records and values into searchable text while keeping their source."],
        ["Database files", "SQLite, SQLite3, DB, MDB, ACCDB", "Reads tables and records in a restricted read-only manner. Older Access files need a Microsoft component."],
        ["Downloaded collections", "ZIP files, repository APIs and bulk exports", "Reads supported items within strict limits and ignores unsafe or unsupported contents."],
        ["SQL files", "Files ending in SQL", "Treats the words as evidence and never runs the commands."],
        ["Audio when enabled", "MP3, MP4, M4A, WAV, FLAC, OGG and others", "Can create a transcript through the paid OpenAI API when deliberately enabled."],
    ], [1.55, 2.25, 2.85])
    add_heading(doc, "Scans and old PDFs", 2)
    add_para(doc, "History Crawler first looks for selectable PDF text, then uses OCR when needed. Clear printed type usually works better than handwriting, faded ink, decorative type, or damaged pages. OCR is a search aid, so important quotations should be checked against the original page.")

    doc.add_page_break()
    add_heading(doc, "Understand the main screen", 1)
    if BROCHURE_MAIN_SCREEN.exists():
        p = doc.add_paragraph()
        p.alignment = WD_ALIGN_PARAGRAPH.CENTER
        p.add_run().add_picture(str(BROCHURE_MAIN_SCREEN), width=Inches(7.0))
        set_last_picture_alt(
            doc,
            "History Crawler main screen",
            "Example History Crawler dashboard showing crawl status, progress counters, menus, and activity messages.",
        )
        p = doc.add_paragraph("Figure 1. The current History Crawler main screen with representative crawl data. The figures show where progress appears and are not promised results.", style="Caption")
        p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    add_para(doc, "This screen is produced by the working application. It shows a crawl in progress with representative values so every counter and progress line is easy to recognize.")
    add_table(doc, ["Screen area", "What it tells you"], [
        ["Crawler status", "The large badge says whether the crawler is stopped, starting, running, stopping, or unable to start."],
        ["Main buttons", "Start or stop the crawl, search the collection, change settings, open support, or view About information."],
        ["Counter boxes", "Show URLs started, responses, successful pages, failures, queued links, keyword matches, retries, and processing errors."],
        ["Progress lines", "Show processed Seed URLs, successful and problem seeds, discovered URLs, local files, and completed file types."],
        ["Activity area", "Shows the current URL and color-coded success, discovery, retry, blocked, challenge, skipped, and error messages."],
    ], [1.75, 4.95])
    add_para(doc, "Large failure counts do not always mean the program is broken. Old websites move, disappear, block automated access, or return temporary errors. The activity message and Problem seeds window explain what happened.")

    doc.add_page_break()
    add_heading(doc, "Manage failed and protected websites", 1)
    add_heading(doc, "Retries and Problem seeds", 2)
    add_para(doc, "Temporary network failures, server errors, and rate limits may receive more attempts. A permanent-looking result, such as 404 Not Found, 410 Gone, a robots rule, or an access block, is recorded for review instead of being retried without limit.")
    add_para(doc, "A seed is not automatically deleted. History Crawler keeps its failure count and history. Cleanup defaults to five consecutive failed crawl cycles, previews every match, and requires confirmation before anything is disabled or deleted.")
    add_table(doc, ["Choice", "Result"], [
        ["Retry next crawl", "Makes the selected failed URL eligible for another crawler run."],
        ["Disable", "Keeps the seed and its history but prevents normal crawling until it is enabled again."],
        ["Delete permanently", "Removes the Seed URL after confirmation. Previously collected pages remain."],
        ["Clean up", "Previews seeds matching a failure-count or first-failure-date rule before applying Disable or Delete."],
        ["Start a new research project", "Resets crawl progress and prior research results after a verified backup. Seed URLs are always protected."],
    ], [2.05, 4.65])
    add_heading(doc, "Websites that need a person", 2)
    add_para(doc, "A CAPTCHA, Cloudflare Turnstile page, login prompt, or similar challenge is recorded instead of repeatedly stopping the crawl. After repeated challenges from one website, History Crawler pauses that host's waiting URLs so other research can continue.")
    add_para(doc, "When access is permitted, choose Verify and crawl in Problem seeds. Complete the website's instruction yourself in the controlled Edge window. A successful session may be reused briefly for waiting pages on the same site. History Crawler does not defeat CAPTCHAs, bypass paywalls, store website passwords, attach to an unrelated personal browser session, or override a site's decision.")
    add_heading(doc, "Historical newspapers", 2)
    add_para(doc, "History Crawler can search the free Library of Congress Chronicling America collection with your active Seed Keywords. In Historical newspaper archives, select individual states or use Select all and Clear all for the Southwest, Northwest, Midwest, Southeast, and Northeast regions. Matching records are saved and eligible pages enter the normal crawl queue. Other state archive and library sites may also be present in the Seed URL catalog.")

    doc.add_page_break()
    add_heading(doc, "Search the collected research", 1)
    add_heading(doc, "Search without AI", 2)
    add_para(doc, "Normal collection search is local and does not use OpenAI credits. Search everything together or choose Web pages, Documents, Audio transcripts, or Structured data. A Searching message and progress indicator show that the program is working.")
    add_table(doc, ["You type", "History Crawler finds"], [
        ["Lincoln railroad Illinois", "Sources containing any of the words, ranked by the strongest matches."],
        ["Lincoln AND railroad", "Sources containing both terms."],
        ["Lincoln OR Douglas", "Sources containing either name."],
        ["Lincoln NOT Nebraska", "Lincoln sources that do not contain Nebraska."],
        ["(Lincoln OR Douglas) AND debate", "Debate sources containing either person."],
        ["\"Gettysburg Address\"", "The exact phrase."],
    ], [2.6, 4.1])
    add_para(doc, "The same search can find a phrase in a crawled webpage, an old PDF, OCR from a photograph, a spreadsheet cell, or a database record. Each result keeps its URL or file path and more detailed source information when available.")
    add_heading(doc, "Use AI as an optional research tool", 2)
    add_para(doc, "AI is not necessary, but it can do substantial additional work when the researcher chooses to enable it. With an OpenAI API key, History Crawler can assist with source summaries, questions about a selected source, people and place extraction, dates and events, relationship extraction, handwriting assistance, optional audio transcription, saved cited findings, and optional meaning-based indexing.")
    add_bullets(doc, [
        "Ask for a short summary of a long source, then verify the summary against the saved text.",
        "Ask for the people, places, dates, events, and relationships mentioned in a source.",
        "Ask a focused question such as What reasons does this letter give for leaving Missouri.",
        "Use handwriting assistance on a difficult image, then compare the answer with the original.",
        "Transcribe a permitted oral-history recording when paid transcription is enabled.",
    ])
    add_para(doc, "AI can make mistakes. Treat every answer as a research lead and check it against the original evidence.", bold_prefix="AI can make mistakes.")

    doc.add_page_break()
    add_heading(doc, "Security storage and cost", 1)
    add_para(doc, "Old websites and research files can contain damaged data or harmful content. History Crawler treats incoming material as untrusted and tries to read it as evidence rather than run it as a program.")
    add_table(doc, ["Protection", "What it means"], [
        ["No automatic program running", "Downloaded programs, installers, scripts, macros, and collected local files are not launched. SQL files are read as text and never executed."],
        ["Restricted database reading", "Supported database files are opened for reading only. Stored commands are not carried out."],
        ["Limits on large files", "The program limits download size, pages, images, database records, spreadsheet cells, ZIP contents, audio work, and processing time."],
        ["Safer ZIP handling", "ZIP files are checked for too many entries, hidden expansion, deep nesting, and extreme compression."],
        ["Careful web access", "The crawler checks redirects, blocks private-network addresses by default, and limits requests to each site."],
        ["Protected OpenAI key", "The key is stored outside the project and encrypted for the current Windows user."],
        ["Recorded source history", "URLs, file paths, dates, redirects, warnings, failures, file types, and data origins remain available for review."],
        ["Protected project reset", "Starting a new topic creates and verifies a backup, keeps Seed URLs, and checks the cleanup before reporting success."],
    ], [2.05, 4.65])
    add_heading(doc, "Storage and technology", 2)
    add_para(doc, "History Crawler is a Windows application built with C# and Microsoft .NET. It stores the collection in Microsoft SQL Server LocalDB on the researcher's computer. The practical collection size depends on free drive space, the installed SQL Server Express or LocalDB limit, and the program's safety limits for individual downloads, pages, archives, images, tables, records, cells, and audio. Backups also need free space.")
    add_para(doc, "Optional tools extend file reading. Tesseract performs OCR. Ghostscript and ImageMagick help prepare difficult PDF and image pages. FFmpeg prepares audio. The Microsoft Access Database Engine reads older Access and Excel files. Install these tools only from trusted publishers.")
    add_heading(doc, "Cost and independence", 2)
    add_para(doc, "History Crawler is free and does not require an AI account. Optional OpenAI API features may cost money based on usage. History Crawler is independent and is not affiliated with, sponsored by, or endorsed by OpenAI. ChatGPT and Codex subscriptions are generally separate from API billing. Check current OpenAI API prices and billing terms before enabling paid features.")
    add_para(doc, "Keep Windows Security enabled and keep tested backups of important research. No crawler can promise that every website or file is harmless.", bold_prefix="Keep Windows Security enabled")

    doc.add_page_break()
    add_heading(doc, "A simple research day", 1)
    add_steps(doc, [
        "Choose the folder that contains your local research. Leave it blank only if you do not want local files read.",
        "Add the Seed Keywords that describe the people, places, events, and subjects you are studying.",
        "Review the Seed URLs and disable web sources you do not want for the project.",
        "Choose the states you want for free historical-newspaper searching.",
        "Select Start crawler. The dashboard shows the current item, completed seeds, discovered links, local-file progress, processed file types, successes, failures, and work waiting in line.",
        "Let the crawler continue while you work. You may stop it safely and resume later.",
        "Open Search collection and type the names, places, dates, events, or phrases you need.",
        "Read each result in context and return to the original file or website before quoting it.",
        "Review Problem seeds when convenient. A failed site does not have to interrupt the whole research session.",
        "Use optional AI only for work and costs you deliberately approve.",
    ])
    add_heading(doc, "Starting a different topic", 2)
    add_para(doc, "Start a new research project creates and verifies a safety backup before clearing selected results. Seed URLs are protected and are never removed by this feature. You may keep or clear Seed Keywords. The separate starter-database tools prepare a clean installation without including a researcher's private results, logs, backups, or API key.")
    add_heading(doc, "Support and project news", 2)
    add_para(doc, "The Support menu includes an optional PayPal donation page and an Updates bugs and suggestions page. Donations open a secure PayPal page for HistoryCrawlerApp@gmail.com; History Crawler does not store payment details. To join the update list, email HistoryCrawlerApp@gmail.com with the subject Subscribe. Send bug reports with the subject Bug Report and suggestions with the subject Suggestion.")
    add_heading(doc, "What the program does not promise", 2)
    add_bullets(doc, [
        "OCR may misread handwriting, faded pages, decorative type, or poor photographs.",
        "A website may block automated access, require a subscription, or disappear.",
        "The crawler cannot decide whether a statement is true, important, biased, or suitable to quote.",
        "Optional AI can make mistakes and must be checked against the saved evidence.",
    ])
    add_para(doc, "The History Crawler User Manual explains every screen and includes a friendly How to appendix for common tasks.", italic=True)

    path = OUT / "History_Crawler_Research_Brochure.docx"
    doc.save(path)
    return path


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    manual = build_manual()
    brochure = build_brochure()
    print(manual)
    print(brochure)
