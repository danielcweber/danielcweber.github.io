// Open external links in a popup modal notice
$(window).on('load', function () {
    $.expr[":"].external = function (a) {
        try {
            return !a.href.match(/^mailto\:/) && !a.href.match(/^tel\:/) && new URL(a.href).host !== (window.location.host);
        }
        catch (ex) {
            return true;
        }
    };

    $("a:external").click(function (z) {
        return confirm("You're about to follow an external link and leave this website. Continue?");
    });
});

