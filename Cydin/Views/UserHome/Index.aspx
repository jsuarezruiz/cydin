<%@ Page Title="" Language="C#" MasterPageFile="~/Views/Shared/Site.Master" Inherits="System.Web.Mvc.ViewPage<Cydin.Models.UserModel>" %>
<%@ Import Namespace="Cydin.Models" %>
<asp:Content ID="Content1" ContentPlaceHolderID="TitleContent" runat="server">
	Projects
</asp:Content>

<asp:Content ID="Content2" ContentPlaceHolderID="MainContent" runat="server">

    <h2>Projects</h2>

    <ul>
    <% foreach (Project p in Model.GetUserProjects ()) { %>
    <li><%=Html.ActionLink (p.Name, "Index", "Project", new { id = p.Id }, null)%></li>
    <% } %>
    </ul>

    <%= Html.ActionLink ("New Project", "Create", "Project") %>

</asp:Content>
