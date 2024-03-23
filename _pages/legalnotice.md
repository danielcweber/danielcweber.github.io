---
title: "Legal Notice / Privacy"
excerpt: "Legal Notice / Privacy"
permalink: /legalnotice/
redirect_from: 
  - /terms/
  - /terms.html
---
{% capture imprint %}{% include legalnotice/imprint.md %}{% endcapture %}
{{ imprint | markdownify }}


{% capture privacy_de %}{% include legalnotice/privacy_de.md %}{% endcapture %}
{{ privacy_de | markdownify }}


{% capture privacy_en %}{% include legalnotice/privacy_en.md %}{% endcapture %}
{{ privacy_en | markdownify }}
