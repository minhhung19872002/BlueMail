"""Labelled spam/ham corpus for measuring BlueMail.Antispam (PROMPT 2.3: report hit rate and false-positive rate).

    python tests/BlueMail.SpamCorpus/generate.py

Writes corpus.jsonl: one scorer input per line ({"label": "ham"|"spam", "kind": ..., fields of SpamInput}).
Everything is synthetic and deterministic (fixed seed): no personal data, no licence questions (same rule as the
mail corpus, docs/00 Z8). It deliberately includes the hard cases: legitimate newsletters and password-reset
notices (ham that looks spammy), and spam with valid SPF/DKIM (spammers own domains too), so content must decide.
The real rate is measured again in production; this corpus guards against regressions when rules change.
"""
import json
import os
import random

R = random.Random(20261123)
OUR = ["bluemail.test", "acme.test"]

FIRST = ["An", "Bình", "Chi", "Dũng", "Hà", "Khoa", "Linh", "Minh", "Nga", "Phúc", "Quân", "Thảo", "Olivia", "Lucas", "Sofía", "Jonas",
         "Иван", "فاطمة", "田中", "王", "김", "Priya", "Tomáš", "João", "Amara"]
PARTNER_DOMAINS = ["doitac.test", "congtyabc.vn", "partner.example", "supplier.example", "university.example", "agency.example", "hospital.example"]
LEGIT_SENDERS = [("news@shop.example", "BlueShop"), ("no-reply@bank.example", "Ngân hàng Example"), ("updates@social.example", "Social"),
                 ("noreply@cloud.example", "Cloud Example"), ("billing@telco.example", "Telco"), ("info@airline.example", "Airline")]

HAM_BUSINESS = [
    ("Báo giá thiết bị tháng {m}", "Chào anh/chị,\nEm gửi báo giá thiết bị mạng tháng {m} như đã trao đổi. Đơn giá đã gồm VAT. Anh/chị xem và phản hồi giúp em trước thứ Sáu.\nTrân trọng,\n{n}"),
    ("Lịch họp dự án tuần {w}", "Chào cả nhóm,\nCuộc họp dự án tuần {w} chuyển sang 14h thứ Ba tại phòng 3. Mọi người chuẩn bị báo cáo tiến độ nhé.\n{n}"),
    ("Biên bản nghiệm thu hợp đồng số {c}", "Kính gửi Quý công ty,\nChúng tôi gửi biên bản nghiệm thu hợp đồng số {c}. Đề nghị Quý công ty ký xác nhận và gửi lại bản scan.\nTrân trọng."),
    ("Re: Đơn hàng {c}", "Dạ em đã nhận được đơn hàng {c}. Hàng sẽ giao trong 3 ngày làm việc. Em cảm ơn anh.\n{n}"),
    ("Meeting notes {w}", "Hi team,\nAttached are the notes from this week's planning meeting. Action items: finalize the budget, review the vendor contract, book the venue.\nThanks,\n{n}"),
    ("Quarterly report draft", "Hello,\nPlease find the draft of the quarterly report. I would appreciate your comments on section 3 before Thursday.\nBest regards,\n{n}"),
    ("Invoice {c} for October services", "Dear customer,\nPlease find invoice {c} for services delivered in October. Payment terms: 30 days. Bank details are on the invoice.\nKind regards,\nAccounts team"),
    ("Angebot für Projekt {c}", "Sehr geehrte Damen und Herren,\nanbei unser Angebot für das Projekt {c}. Bei Fragen stehe ich gerne zur Verfügung.\nMit freundlichen Grüßen\n{n}"),
    ("Compte rendu de la réunion", "Bonjour,\nVoici le compte rendu de la réunion de mardi. Merci de vérifier les décisions concernant le calendrier.\nCordialement,\n{n}"),
    ("会議の議事録 {w}", "お疲れ様です。本日の会議の議事録を共有します。来週までに各自の担当項目をご確認ください。\n{n}"),
    ("合同第{c}号", "您好，合同第{c}号已经签署，请查收附件并确认交货时间。谢谢！\n{n}"),
    ("Отчёт за месяц", "Добрый день! Направляю отчёт за прошлый месяц. Прошу посмотреть раздел о расходах.\nС уважением, {n}"),
    ("تأكيد موعد الاجتماع", "مرحباً، نؤكد موعد الاجتماع يوم الثلاثاء الساعة العاشرة صباحاً. شكراً لكم.\n{n}"),
    ("Lunch tomorrow?", "Hey, are you free for lunch tomorrow around noon? There's a new place near the office.\n{n}"),
    ("Hồ sơ ứng tuyển vị trí kỹ sư", "Kính gửi phòng Nhân sự,\nTôi xin gửi hồ sơ ứng tuyển vị trí kỹ sư phần mềm. Rất mong được trao đổi thêm.\nTrân trọng,\n{n}"),
]

HAM_BULK = [
    ("Bản tin tháng {m}: sản phẩm mới", "Chào bạn, tháng {m} BlueShop ra mắt bộ sưu tập mới. Xem chi tiết trên trang web. Bạn nhận thư này vì đã đăng ký nhận bản tin. Hủy đăng ký bất cứ lúc nào."),
    ("Your monthly statement is ready", "Your statement for {m} is now available in online banking. For your security we never ask for your password by email."),
    ("Sale ends Sunday: 20% off selected items", "Our seasonal sale ends this Sunday. Browse selected items with 20% off. You are receiving this because you subscribed. Unsubscribe anytime."),
    ("Reset your password", "We received a request to reset the password for your account. If this was you, use the link below within 30 minutes. If not, you can ignore this email."),
    ("Your flight booking confirmation", "Thank you for booking with us. Your flight departs on the {d}th at 08:40. Check in online 24 hours before departure."),
    ("Mã xác nhận đăng nhập", "Mã xác nhận của bạn là {code}. Mã có hiệu lực trong 10 phút. Không chia sẻ mã này với bất kỳ ai."),
]

SPAM = [
    ("lottery", "CONGRATULATIONS!!! YOU HAVE WON $1,000,000", "Dear winner, your email address has won one million dollars in the international lottery. Send your full name, address and bank account to claim your prize immediately."),
    ("lottery", "Chúc mừng bạn đã trúng thưởng iPhone!!!", "Bạn là khách hàng may mắn trúng thưởng iPhone 16. Bấm vào liên kết và nhập thông tin thẻ ngân hàng để nhận quà ngay hôm nay, số lượng có hạn."),
    ("pharma", "Cheap meds without prescription", "Buy cheap pills online without prescription. Best prices, discreet shipping worldwide. Order now and save 80%."),
    ("crypto", "Double your bitcoin in 24 hours", "Invest in our exclusive crypto program and double your bitcoin in 24 hours. Guaranteed returns, limited spots. Send BTC to the wallet below."),
    ("crypto", "Cơ hội đầu tư lợi nhuận 30%/tháng", "Đầu tư tiền ảo cùng chuyên gia, lợi nhuận đảm bảo 30% mỗi tháng. Chuyển khoản ngay để giữ suất, chỉ còn 5 suất cuối cùng."),
    ("phish", "Your mailbox is full - verify your account", "Your mailbox is full and you will stop receiving messages. Verify your account now to keep your password and avoid suspension."),
    ("phish", "Thông báo: mật khẩu sắp hết hạn", "Mật khẩu hộp thư của bạn sắp hết hạn. Xác minh tài khoản ngay để giữ mật khẩu cũ, nếu không tài khoản của bạn sẽ bị khóa trong 24 giờ."),
    ("phish", "Unusual sign-in activity", "We detected unusual sign-in activity on your account. Confirm your password within 24 hours or your account will be suspended."),
    ("phish", "Update your payment information", "Your payment failed. Update your payment information now to avoid service interruption. Click here to update."),
    ("fraud", "Urgent business proposal", "I am a bank manager and I need your assistance to transfer 15 million US dollars. You will receive 40 percent. Reply with your bank details urgently and keep this confidential."),
    ("fraud", "Inheritance claim notification", "You have been named beneficiary of an unclaimed inheritance of 4.5 million euros. Contact our lawyer with your passport copy to release the funds."),
    ("extort", "I have recorded you", "I hacked your device and recorded you. Pay 1500 USD in bitcoin within 48 hours or I will send the video to all your contacts."),
    ("seo", "Rank #1 on Google guaranteed", "We guarantee first page Google ranking for your website in 7 days. Cheap SEO packages, backlinks, traffic. Reply for a free audit."),
    ("invoice", "Invoice overdue - payment required", "Your invoice is overdue. Open the attached document and enable content to view the invoice. Payment required today."),
    ("jobscam", "Work from home $500/day", "Earn 500 dollars per day working from home, no experience needed. Just reply with your WhatsApp number to start immediately."),
]


def rand_name():
    return R.choice(FIRST) + " " + R.choice(["Nguyễn", "Trần", "Lê", "Smith", "Müller", "Dubois", "Petrov", "Kim", "Silva", "Okafor"])


def fill(t):
    return t.format(m=R.randint(1, 12), w=R.randint(1, 52), c=R.randint(1000, 9999), n=rand_name(), d=R.randint(1, 28),
                    code=R.randint(100000, 999999))


def auth_good(domain):
    return {"spf": "pass", "dkim": [{"result": "pass", "domain": domain}], "dmarc": "pass", "dmarcPolicy": "none", "ptr": "mail." + domain}


def ham():
    kind = R.choices(["business", "bulk", "internal"], [55, 30, 15])[0]
    if kind == "business":
        subj, body = R.choice(HAM_BUSINESS)
        domain = R.choice(PARTNER_DOMAINS)
        name = rand_name()
        a = auth_good(domain)
        # Small companies: sometimes no DKIM, SPF softfail, no PTR - must still be ham.
        if R.random() < 0.25:
            a["dkim"] = []
            a["dmarc"] = "none"
        if R.random() < 0.1:
            a["spf"] = "softfail"
        if R.random() < 0.15:
            a["ptr"] = None
        links = [{"href": f"https://{domain}/docs/{R.randint(1, 999)}", "text": "tài liệu" if R.random() < 0.5 else "document"}] if R.random() < 0.3 else []
        return dict(label="ham", kind=kind, fromAddress=f"{R.choice(['an','minh','sales','info','hr','pm'])}@{domain}", fromName=name, subject=fill(subj),
                    text=fill(body), html=None, images=0, links=links, known=R.random() < 0.6, firstSeen=R.random() < 0.1, **a)
    if kind == "bulk":
        subj, body = R.choice(HAM_BULK)
        addr, name = R.choice(LEGIT_SENDERS)
        domain = addr.split("@")[1]
        links = [{"href": f"https://www.{domain}/offer/{R.randint(1, 99)}", "text": "View details"},
                 {"href": f"https://www.{domain}/unsubscribe?u={R.randint(1, 9999)}", "text": "Unsubscribe"}]
        html = "<html><body><p>" + fill(body) + "</p><img src=x><img src=y></body></html>"
        return dict(label="ham", kind=kind, fromAddress=addr, fromName=name, subject=fill(subj), text=fill(body), html=html, images=2,
                    links=links, known=False, firstSeen=False, **auth_good(domain))
    subj, body = R.choice(HAM_BUSINESS)
    domain = R.choice(OUR)
    return dict(label="ham", kind=kind, fromAddress=f"colleague@{domain}", fromName=rand_name(), subject=fill(subj), text=fill(body), html=None,
                images=0, links=[], known=True, firstSeen=False, **auth_good(domain))


def spam():
    kind, subj, body = R.choice(SPAM)
    tld = R.choice(["top", "xyz", "click", "example", "info", "biz"])
    domain = f"{R.choice(['promo','win','secure','acct','mail','offer'])}{R.randint(1, 999)}.{tld}"
    a = {"spf": R.choice(["none", "softfail", "fail", "pass"]), "dkim": [], "dmarc": "none", "dmarcPolicy": "none",
         "ptr": None if R.random() < 0.6 else "host" + str(R.randint(1, 255)) + ".dynamic.example"}
    if R.random() < 0.35:  # spammer-owned domain with valid authentication: content must decide
        a = auth_good(domain)
    name = R.choice(["Security Team", "Bộ phận hỗ trợ", "IT Helpdesk", "Customer Service", "Lottery Board", rand_name()])
    reply = None
    if kind in ("fraud", "jobscam", "lottery") and R.random() < 0.6:
        reply = f"claims{R.randint(1, 99)}@gmail.com"
    from_address = f"{R.choice(['support','noreply','admin','service','info'])}@{domain}"
    if kind == "phish" and R.random() < 0.4:
        # look-alike of our own domain, or a display name naming a real brand
        from_address = R.choice(["it@bluemaii.test", "admin@bIuemail.test", "support@acme-test.test"])
        name = R.choice(["paypal.com Support", "Microsoft account team", name])
    links = []
    if kind in ("phish", "lottery", "crypto", "pharma", "jobscam", "invoice"):
        target = R.choice([f"http://{R.randint(1, 223)}.{R.randint(0, 255)}.{R.randint(0, 255)}.{R.randint(1, 254)}/login", f"https://{domain}/verify",
                           "https://bit.ly/" + str(R.randint(10000, 99999)), f"https://xn--pypal-4ve.{tld}/account"])
        text = R.choice(["https://www.paypal.com/account", "Click here", "Xác minh ngay", "https://login.microsoftonline.com", "Claim now"])
        links.append({"href": target, "text": text})
    html = None
    images = 0
    text = body
    if R.random() < 0.3:
        html = "<html><body><img src=promo.png></body></html>"
        images = 1
        text = ""
    elif R.random() < 0.5:
        html = "<html><body><p>" + body + "</p></body></html>"
    att = ["double_extension"] if kind == "invoice" and R.random() < 0.6 else []
    if R.random() < 0.3:
        subj = subj.upper()
    return dict(label="spam", kind=kind, fromAddress=from_address, fromName=name, replyTo=reply, subject=subj, text=text, html=html, images=images,
                links=links, attachments=att, known=False, firstSeen=True, missingDate=R.random() < 0.15, missingMsgId=R.random() < 0.2,
                helo=R.choice(["localhost", "[1.2.3.4]", "mail." + domain, "bluemail.test", "WIN-" + str(R.randint(1000, 9999))]), **a)


def main():
    out = os.path.join(os.path.dirname(__file__), "corpus.jsonl")
    rows = [ham() for _ in range(700)] + [spam() for _ in range(700)]
    R.shuffle(rows)
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    print(f"wrote {len(rows)} messages to {out}")


if __name__ == "__main__":
    main()
