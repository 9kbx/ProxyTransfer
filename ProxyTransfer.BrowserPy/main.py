from cloakbrowser import launch

browser = launch(
    proxy="http://127.0.0.1:55486",  # residential IP, not datacenter
    geoip=False,       # match timezone + locale to proxy IP
    headless=False,    # some sites detect headless even with C++ patches
    humanize=True,     # human-like mouse, keyboard, scroll
)
page = browser.new_page()
page.goto("https://api.ipify.org/")

# 这里暂停，等待用户查看浏览器界面
input("看完浏览器界面后按回车关闭浏览器...")

browser.close()